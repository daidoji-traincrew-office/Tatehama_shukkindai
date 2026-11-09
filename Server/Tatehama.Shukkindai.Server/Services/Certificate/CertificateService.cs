using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Tatehama.Shukkindai.Server.Configuration;
using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Repositories.Certificate;

namespace Tatehama.Shukkindai.Server.Services.Certificate;

public interface ICertificateService
{
    /// <summary>
    /// 起動時の証明書。初回の呼び出しで、ローテーション(足りなければ生成)を 1 回同期的に行い、OpenIddict に登録する
    /// 証明書を読み込んで返す。以降は同じものを返す(動いているプロセスの証明書は、再起動まで入れ替えない)。
    /// 有効な署名証明書が無ければ、分かりやすいメッセージの例外にする。
    /// </summary>
    CertificateSet GetStartupCertificates();

    /// <summary>直近の状態(ディスク上の一覧と、プロセスが使っている証明書)。</summary>
    CertificateStatus GetStatus();

    /// <summary>
    /// 定期実行の 1 回分。ローテーション(次の鍵の生成と、期限を過ぎた鍵の削除)、状態のログ、
    /// 「再起動が必要か」の判定を行う。用途ごとの失敗はログに出して、他の用途は続ける。
    /// </summary>
    void Maintain();
}

/// <summary>
/// 証明書の「判断と順序」を受け持つ。ファイルの読み書きは <see cref="ICertificateRepository"/>、
/// 作る・消すの方針は <see cref="CertificateRotationPolicy"/>(純粋関数)に任せ、ここでは呼ぶ順番とログ・状態の更新を決める。
/// 呼ばれるのは 2 か所。起動時に <c>GetStartupCertificates</c>(OpenIddict の options の組み立てと
/// <see cref="HostedService.CertificateRotationHostedService"/> の StartAsync から。初回だけ実行し、以降は同じ結果を返す)、
/// 動作中に <c>Maintain</c>(HostedService が 24 時間ごと)。
/// </summary>
public sealed class CertificateService(
    ICertificateRepository repository,
    TimeProvider timeProvider,
    IOptions<AuthenticationSettings> settings,
    ILogger<CertificateService> logger) : ICertificateService
{
    private static readonly CertificatePurpose[] Purposes = Enum.GetValues<CertificatePurpose>();

    /// <summary>時計が過去に戻った/大きくずれたとみなす幅(非署名の既存の証明書の NotBefore が、今よりこれ以上未来)。</summary>
    private static readonly TimeSpan ClockSanityMargin = TimeSpan.FromDays(1);

    private readonly object _gate = new();
    private CertificateSet? _startup;
    private DateTimeOffset _processStart;
    private readonly Dictionary<CertificatePurpose, HashSet<string>> _inUse = new();
    private readonly Dictionary<CertificatePurpose, string> _inUseActive = new();
    private CertificateStatus _status = CertificateStatus.Empty;

    public CertificateStatus GetStatus()
    {
        lock (_gate)
        {
            return _status;
        }
    }

    public CertificateSet GetStartupCertificates()
    {
        lock (_gate)
        {
            return _startup ??= LoadStartupCertificates();
        }
    }

    /// <summary>ローテーション中に起きた失敗(用途ごとの失敗は続行し、起動時の判断材料にする)。</summary>
    private sealed record RotationOutcome(bool Success, Exception? Failure, bool LockOrPublicFailed);

    private CertificateSet LoadStartupCertificates()
    {
        var now = timeProvider.GetUtcNow();
        _processStart = now;
        var configured = settings.Value.CertificateDirectory;
        if (!Path.IsPathRooted(configured))
        {
            CertificateLog.CertificateDirectoryIsRelative(logger, configured, Path.GetFullPath(configured));
        }

        // --- 1. 何も変えずに確かめる ---
        // 生成や削除より先に行う。ここで起動を止めるときは、何も作らず、何も消さないため。
        PreflightChecks(now);

        // --- 2. ローテーションと public/ の同期(ここから先はディスクを変える) ---
        // 足りない鍵の生成、保持期間を過ぎた鍵の削除、public/ の .cer の書き直しを、ロックの中でまとめて行う。
        // ロックが取れない・public/ に書けないときは起動を止める(公開できていない鍵で署名しないため)。
        var outcome = RotateAll(now);
        if (outcome.LockOrPublicFailed)
        {
            throw new InvalidOperationException(
                $"証明書のディレクトリ({Path.GetFullPath(configured)})を更新できません(ロックが取れない、public/ に .cer を書けない、など)。" +
                "Crew と TTC が一度も見ていない鍵で署名しないよう、起動を中止します。" +
                $" 原因: {outcome.Failure?.Message} 手順書: Docs/certificate-rotation.md", outcome.Failure);
        }

        // --- 3. ローテーション後の状態を読み直し、動かせるかを確かめる ---
        // 生成・削除の結果を反映した一覧で判断する。動かせない状態は起動を止める(黙って作り直して起動することはしない)。
        // 止めるときのメッセージには、直前の失敗の実際の原因を入れる。
        var all = Purposes.ToDictionary(p => p, repository.GetAll);
        var cause = outcome.Failure is null ? string.Empty : $" 直前の失敗: {outcome.Failure.Message}";
        // 署名は、期限内で、public/ に同じ内容の .cer がある鍵だけを候補にする(Crew と TTC が一度も見ていない鍵では署名しない)
        var signingCandidates = all[CertificatePurpose.ServerSigning]
            .Where(c => c.NotAfter >= now && repository.IsPublished(c)).ToList();
        if (CertificateRotationPolicy.SelectActive(signingCandidates, now) is null)
        {
            CertificateLog.NoValidSigningCertificate(logger);
            var unpublished = all[CertificatePurpose.ServerSigning]
                .Where(c => c.NotAfter >= now && CertificateRotationPolicy.IsValidAt(c, now) && !repository.IsPublished(c)).ToList();
            throw new InvalidOperationException(unpublished.Count > 0
                ? "有効なサーバー署名証明書はありますが、cert/public/ に同じ内容の .cer がありません。" +
                  $"Crew と TTC が見ていない鍵では署名しないため、起動を中止します。{cause} Docs/certificate-rotation.md を参照してください。"
                : "有効なサーバー署名証明書がありません(すべて期限切れ、またはすべて NotBefore が未来です)。" +
                  $"起動を中止します。{cause} Docs/certificate-rotation.md の「全部期限切れで起動しないとき」を参照してください。" +
                  $" 証明書: {Describe(all[CertificatePurpose.ServerSigning])}");
        }

        if (CertificateRotationPolicy.SelectActive(all[CertificatePurpose.ServerEncryption], now) is null)
        {
            throw new InvalidOperationException(
                $"有効なサーバー暗号化証明書がありません。起動を中止します。{cause} Docs/certificate-rotation.md を参照してください。");
        }

        foreach (var purpose in new[] { CertificatePurpose.ClientSigning, CertificatePurpose.ClientEncryption })
        {
            if (all[purpose].Count == 0)
            {
                throw new InvalidOperationException(
                    $"{CertificateLog.Name(purpose)} の証明書がありません。起動を中止します。{cause} Docs/certificate-rotation.md を参照してください。");
            }
        }

        // --- 4. 候補を選んで読み込み、「このプロセスが使う鍵」として記録する ---
        // _inUse / _inUseActive は、(a) 再起動まで消さない鍵の目印(Plan の inUseThumbprints)、
        // (b) 「再起動が必要か」の比較元(IsRestartRequired)になる。切り替えが再起動のときだけなのはこのため。
        var loaded = new Dictionary<CertificatePurpose, IReadOnlyList<X509Certificate2>>();
        foreach (var purpose in Purposes)
        {
            // 署名は、公開済みの有効なものと、NotBefore が未来の次の鍵。期限切れは載せない。
            // 暗号化は、保持期間内のもの全部(期限切れでも復号には使える)。Client は期限を見ないので全部。
            var targets = purpose == CertificatePurpose.ServerSigning ? signingCandidates : all[purpose].ToList();
            loaded[purpose] = targets.Select(repository.Load).ToList();
            _inUse[purpose] = targets.Select(c => c.Thumbprint).ToHashSet();
            var active = CertificateRotationPolicy.SelectActive(targets, now);
            if (active is not null)
            {
                _inUseActive[purpose] = active.Thumbprint;
            }

            CertificateLog.StartupCertificatesLoaded(logger, CertificateLog.Name(purpose), targets.Count,
                active?.Thumbprint ?? "none");
        }

        // --- 5. 状態を保存して返す(返した証明書が OpenIddict に登録される) ---
        _status = BuildStatus(all, outcome.Success ? now : null);
        return new CertificateSet(loaded);
    }

    /// <summary>
    /// 起動時の、副作用のない確認。ここで止めれば、何も作らず何も消さない。
    /// ・初回の生成が許可されていないのに、用途のディレクトリが空(cert/ のマウント漏れの可能性)
    /// ・署名証明書が 1 つも有効でない(時計が大きく進んだ場合に、暗号化用と Client 用を先に消して作り直さないため)
    /// ・時計が過去にずれている
    /// </summary>
    private void PreflightChecks(DateTimeOffset now)
    {
        // --- 1. 一覧を読む(壊れた pfx・読めない権限はここで起動を止める) ---
        Dictionary<CertificatePurpose, IReadOnlyList<CertificateInfo>> all;
        try
        {
            all = Purposes.ToDictionary(p => p, repository.GetAll);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidOperationException($"証明書を読み込めません。起動を中止します。{e.Message}", e);
        }

        // --- 2. 空のディレクトリは、初回の生成を許可されたときだけ通す ---
        // cert/ のマウント漏れで空のボリュームになっても、黙って新しい署名鍵で起動しないため(AllowCertificateBootstrap の理由)。
        if (!settings.Value.AllowCertificateBootstrap)
        {
            var empty = Purposes.Where(p => all[p].Count == 0).Select(CertificateLog.Name).ToList();
            if (empty.Count > 0)
            {
                throw new InvalidOperationException(
                    $"証明書が 1 つもありません({string.Join(", ", empty)})。" +
                    $"証明書のディレクトリ: {Path.GetFullPath(settings.Value.CertificateDirectory)}。" +
                    "初回のデプロイでないなら、cert/ のボリュームのマウント漏れの可能性があります" +
                    "(このまま新しい署名鍵で起動すると、Crew と TTC は全員 401 になります)。" +
                    "初回のデプロイで鍵を作りたいときだけ、Authentication:AllowCertificateBootstrap=true を付けて起動してください。" +
                    "手順書: Docs/certificate-rotation.md");
            }
        }

        // --- 3. 時計が過去にずれていないか ---
        // 署名以外は作った時点で NotBefore が過去なので、今より未来の NotBefore があれば時計がおかしい。
        // 時計がずれたまま鍵の作成・削除を判断しないよう、何も変えずに止める。
        var future = Purposes.Where(p => p != CertificatePurpose.ServerSigning)
            .SelectMany(p => all[p]).Where(c => c.NotBefore > now + ClockSanityMargin).ToList();
        if (future.Count > 0)
        {
            throw new InvalidOperationException(
                $"システム時刻({CertificateLog.Iso(now)})が、既存の証明書の NotBefore({CertificateLog.Iso(future.Max(c => c.NotBefore))})より過去です。" +
                "時計がずれている可能性があるため、起動を中止します(何も作らず、何も消していません)。時計を直してください。");
        }

        // --- 4. 署名の鍵が今使えるか ---
        // 署名が全部期限切れのまま先へ進むと、暗号化用と Client 用だけが作り直されてしまう。それを避けるため、先に止める。
        if (all[CertificatePurpose.ServerSigning].Count > 0 &&
            CertificateRotationPolicy.SelectActive(all[CertificatePurpose.ServerSigning], now) is null)
        {
            CertificateLog.NoValidSigningCertificate(logger);
            throw new InvalidOperationException(
                "有効なサーバー署名証明書がありません(すべて期限切れ、またはすべて NotBefore が未来です)。" +
                "起動を中止します(何も作らず、何も消していません)。" +
                "Docs/certificate-rotation.md の「全部期限切れで起動しないとき」を参照してください。" +
                $" 現在時刻: {CertificateLog.Iso(now)} 証明書: {Describe(all[CertificatePurpose.ServerSigning])}");
        }
    }

    public void Maintain()
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();

            // --- 1. ローテーション(次の鍵の生成、期限を過ぎた鍵の削除、public/ の同期) ---
            var success = RotateAll(now).Success;

            // --- 2. ディスクの現状から状態(メトリクスの元になる)を作り直す ---
            // 失敗しても例外にはせず、前回の状態のまま続ける(定期実行を止めない)。
            try
            {
                var all = Purposes.ToDictionary(p => p, repository.GetAll);
                var lastSuccess = success ? now : _status.LastRotationSuccess;
                _status = BuildStatus(all, lastSuccess);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                CertificateLog.CertificateRotationFailed(logger, e, "all");
            }

            // --- 3. 状態のログと、再起動が必要かの通知 ---
            LogStatus(_status, now);
        }
    }

    /// <summary>全用途のローテーションを、ロックの中で 1 回行う。用途ごとの失敗はログに出して続ける。</summary>
    private RotationOutcome RotateAll(DateTimeOffset now)
    {
        try
        {
            // ロックは、同じ cert/ を見る別のプロセスと、同時に鍵を作らないため。取ったあとに一覧を読み直す(Rotate の中)
            return repository.WithLock(() =>
            {
                Exception? failure = null;
                var publicFailed = false;
                var directoryEmpty = false;

                // --- 1. 用途ごとに、作る・消すを実行する ---
                // 1 つの用途が失敗しても、他の用途は続ける(失敗は最初の 1 件を覚えて、呼び出し元の判断に渡す)。
                foreach (var purpose in Purposes)
                {
                    try
                    {
                        // 空のディレクトリ(動作中に pfx が消えた)はローテーションの失敗として扱う
                        directoryEmpty |= !Rotate(purpose, now);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        CertificateLog.CertificateRotationFailed(logger, e, CertificateLog.Name(purpose));
                        failure ??= e;
                    }
                }

                // --- 2. public/ の .cer を、署名の pfx に合わせる ---
                // 1 の後に行う。新しく作った署名鍵の .cer を書き、消した鍵の .cer を消すため。
                // 失敗は別扱い(publicFailed)にする。起動時は、公開できていない鍵で署名しないために止める理由になる。
                try
                {
                    SyncPublic();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    CertificateLog.CertificateRotationFailed(logger, e, "public");
                    failure ??= e;
                    publicFailed = true;
                }

                return new RotationOutcome(failure is null && !directoryEmpty, failure, publicFailed);
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // ロックが取れない、cert/ を作れない、など
            CertificateLog.CertificateRotationFailed(logger, e, "all");
            return new RotationOutcome(false, e, true);
        }
    }

    /// <summary>1 つの用途のローテーションを行う。ディレクトリが空で生成も許可されていない(作れなかった)ときは false。</summary>
    private bool Rotate(CertificatePurpose purpose, DateTimeOffset now)
    {
        var name = CertificateLog.Name(purpose);

        // --- 1. 今のディスクの状態から、作る・消すを決める(判断は CertificateRotationPolicy) ---
        // ロックを取ったあとに読み直した一覧を使う。このプロセスが使っている鍵(_inUse)は消し候補から外れる。
        var certificates = repository.GetAll(purpose);
        var inUse = _inUse.TryGetValue(purpose, out var set) ? set : [];
        var plan = CertificateRotationPolicy.Plan(purpose, certificates, inUse, now,
            _processStart == default ? now : _processStart, settings.Value.AllowCertificateBootstrap);

        // --- 2. 空のディレクトリを知らせる(動作中に pfx が消えた。鍵は作らない) ---
        // 起動時は PreflightChecks が先に止めるので、ここに来るのは動作中だけ。次の再起動で起動に失敗するため Error。
        if (plan.BootstrapDenied)
        {
            CertificateLog.CertificateDirectoryEmpty(logger, name,
                Path.Combine(Path.GetFullPath(settings.Value.CertificateDirectory), name));
            return false;
        }

        // --- 3. 作る(生成が先。消した結果、期限内の鍵が無くならないようにするため) ---
        if (plan.Create is { } create)
        {
            var created = repository.Create(purpose, create.NotBefore, create.NotAfter);
            // 署名は人(Crew・TTC の運用者)にも関わるので Warning。切り替えまでの猶予が足りないときは Error
            // 初回の生成(AllowCertificateBootstrap)も、意図したものか確かめられるよう Warning にする
            var level = create.Initial || purpose == CertificatePurpose.ServerSigning
                ? create.ShortNotice ? LogLevel.Error : LogLevel.Warning
                : LogLevel.Information;
            CertificateLog.CertificateCreated(logger, level, name, created.Thumbprint,
                CertificateLog.Iso(created.NotBefore), CertificateLog.Iso(created.NotAfter));
        }

        // --- 4. 消す(保持期間を過ぎたものだけ。Plan が決めた分) ---
        foreach (var expired in plan.Delete)
        {
            repository.Delete(expired);
            CertificateLog.CertificateDeleted(logger, name, expired.Thumbprint, CertificateLog.Iso(expired.NotAfter));
        }

        return true;
    }

    /// <summary>Crew と TTC が読む <c>public/</c> の .cer を、署名用の pfx と一致させる。pfx を消したら .cer も消える。</summary>
    private void SyncPublic()
    {
        var changes = repository.SyncPublic(repository.GetAll(CertificatePurpose.ServerSigning));
        if (changes.Count > 0)
        {
            CertificateLog.PublicCertificatesChanged(logger, string.Join(", ", changes));
        }
    }

    private CertificateStatus BuildStatus(
        Dictionary<CertificatePurpose, IReadOnlyList<CertificateInfo>> all, DateTimeOffset? lastSuccess)
    {
        var purposes = Purposes.Select(p => new PurposeStatus(p, all[p],
            _inUse.TryGetValue(p, out var set) ? set : new HashSet<string>(),
            _inUseActive.TryGetValue(p, out var active) ? active : null)).ToList();
        return new CertificateStatus(purposes, lastSuccess);
    }

    /// <summary>
    /// 状態のログ(7101〜)を出す。ここで出るログがアラートの入力になる。メトリクスは <see cref="CertificateMetrics"/> が
    /// <c>GetStatus</c> の結果を読んで出す。
    /// </summary>
    private void LogStatus(CertificateStatus status, DateTimeOffset now)
    {
        // --- 1. ディレクトリ全体の点検(権限が緩い、public/ に想定外のファイルがある) ---
        foreach (var path in SafeLoosePermissions())
        {
            CertificateLog.CertificatePermissionTooOpen(logger, path);
        }

        foreach (var name in SafeUnexpectedPublicFiles())
        {
            CertificateLog.UnexpectedPublicFile(logger, name);
        }

        // --- 2. 用途ごと: 証明書 1 枚ごとの状態 ---
        foreach (var purpose in status.Purposes)
        {
            var name = CertificateLog.Name(purpose.Purpose);
            var activeByTime = CertificateRotationPolicy.SelectActive(purpose.Certificates, now);
            foreach (var certificate in purpose.Certificates.OrderBy(c => c.NotBefore))
            {
                var state = CertificateRotationPolicy.StateOf(certificate, activeByTime, now);
                CertificateLog.CertificateStatus(logger, LogLevel.Information, name, certificate.Thumbprint,
                    state.ToString(), CertificateLog.Iso(certificate.NotBefore), CertificateLog.Iso(certificate.NotAfter),
                    CertificateLog.Days(certificate.NotAfter - now));
            }

            // --- 3. 用途ごと: 使用中の鍵の残り日数と、後継の有無 ---
            // inUse = このプロセスが実際に使っている鍵。起動時に有効な鍵を選べなかったときは、時刻で選んだもの。
            var inUse = purpose.InUseActive ?? activeByTime;
            if (inUse is null)
            {
                continue;
            }

            var inUseRemaining = inUse.NotAfter - now;
            var successor = purpose.Certificates.Any(c =>
                c.Thumbprint != inUse.Thumbprint && c.NotAfter > (inUse.NotAfter > now ? inUse.NotAfter : now));
            var newest = purpose.Certificates.MaxBy(c => c.NotAfter)!;
            CertificateLog.CertificateSetStatus(logger, CertificateLog.SetStatusLevel(inUseRemaining, successor), name,
                inUse.Thumbprint, CertificateLog.Iso(inUse.NotAfter), CertificateLog.Days(inUseRemaining),
                CertificateLog.Iso(newest.NotAfter), CertificateLog.Days(newest.NotAfter - now), successor);

            // --- 4. 再起動が必要か ---
            // 今の時刻なら使うべき鍵が、プロセスが使っている鍵と違えば、再起動で切り替わる(7105。メトリクス restart_required)。
            if (CertificateRotationPolicy.IsRestartRequired(purpose, now))
            {
                CertificateLog.RestartRequired(logger, CertificateLog.RestartRequiredLevel(inUseRemaining), name,
                    activeByTime!.Thumbprint, inUse.Thumbprint, CertificateLog.Iso(inUse.NotAfter),
                    CertificateLog.Days(inUseRemaining));
            }

            // --- 5. 署名だけ: 公開済みで、まだ有効になっていない次の鍵(切り替え前の予告、7103) ---
            if (purpose.Purpose == CertificatePurpose.ServerSigning &&
                CertificateRotationPolicy.NextPending(purpose.Certificates, now) is { } next)
            {
                var untilActivation = next.NotBefore - now;
                CertificateLog.NextSigningCertificatePending(logger, CertificateLog.NextSigningLevel(untilActivation),
                    next.Thumbprint, CertificateLog.Iso(next.NotBefore), CertificateLog.Days(untilActivation),
                    CertificateRepository.CerFileName(next.Thumbprint));
            }
        }
    }

    private IReadOnlyList<string> SafeLoosePermissions()
    {
        try
        {
            return repository.FindLoosePermissions();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private IReadOnlyList<string> SafeUnexpectedPublicFiles()
    {
        try
        {
            return repository.FindUnexpectedPublicFiles();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Describe(IReadOnlyList<CertificateInfo> certificates) =>
        certificates.Count == 0
            ? "(なし)"
            : string.Join("; ", certificates.Select(c =>
                $"{c.Thumbprint} notBefore={CertificateLog.Iso(c.NotBefore)} notAfter={CertificateLog.Iso(c.NotAfter)}"));
}
