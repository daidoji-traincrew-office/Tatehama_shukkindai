using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Tatehama.Shukkindai.Server.Configuration;
using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Repositories.Certificate;

namespace Tatehama.Shukkindai.Server.Services;

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

        // 1. 何も変えずに確かめる(止めるときは、何も作らず、何も消さない)
        PreflightChecks(now);

        // 2. ローテーション(足りなければ生成。保持期間を過ぎたものの削除)と、public/ の同期
        var outcome = RotateAll(now);
        if (outcome.LockOrPublicFailed)
        {
            throw new InvalidOperationException(
                $"証明書のディレクトリ({Path.GetFullPath(configured)})を更新できません(ロックが取れない、public/ に .cer を書けない、など)。" +
                "Crew と TTC が一度も見ていない鍵で署名しないよう、起動を中止します。" +
                $" 原因: {outcome.Failure?.Message} 手順書: Docs/certificate-rotation.md", outcome.Failure);
        }

        var all = Purposes.ToDictionary(p => p, repository.GetAll);

        // 3. 動かせない状態は、起動を止める(黙って作り直して起動することはしない)。原因は実際のものを書く
        var cause = outcome.Failure is null ? string.Empty : $" 直前の失敗: {outcome.Failure.Message}";
        // 署名は、public/ に同じ内容の .cer がある鍵だけを候補にする(Crew と TTC が一度も見ていない鍵では署名しない)
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
        Dictionary<CertificatePurpose, IReadOnlyList<CertificateInfo>> all;
        try
        {
            all = Purposes.ToDictionary(p => p, repository.GetAll);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidOperationException($"証明書を読み込めません。起動を中止します。{e.Message}", e);
        }

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

        // 署名以外は作った時点で NotBefore が過去なので、今より未来の NotBefore があれば時計がおかしい
        var future = Purposes.Where(p => p != CertificatePurpose.ServerSigning)
            .SelectMany(p => all[p]).Where(c => c.NotBefore > now + ClockSanityMargin).ToList();
        if (future.Count > 0)
        {
            throw new InvalidOperationException(
                $"システム時刻({CertificateLog.Iso(now)})が、既存の証明書の NotBefore({CertificateLog.Iso(future.Max(c => c.NotBefore))})より過去です。" +
                "時計がずれている可能性があるため、起動を中止します(何も作らず、何も消していません)。時計を直してください。");
        }

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
            var success = RotateAll(now).Success;
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

            LogStatus(_status, now);
        }
    }

    /// <summary>全用途のローテーションを、ロックの中で 1 回行う。用途ごとの失敗はログに出して続ける。</summary>
    private RotationOutcome RotateAll(DateTimeOffset now)
    {
        try
        {
            return repository.WithLock(() =>
            {
                Exception? failure = null;
                var publicFailed = false;
                foreach (var purpose in Purposes)
                {
                    try
                    {
                        Rotate(purpose, now);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        CertificateLog.CertificateRotationFailed(logger, e, CertificateLog.Name(purpose));
                        failure ??= e;
                    }
                }

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

                return new RotationOutcome(failure is null, failure, publicFailed);
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // ロックが取れない、cert/ を作れない、など
            CertificateLog.CertificateRotationFailed(logger, e, "all");
            return new RotationOutcome(false, e, true);
        }
    }

    private void Rotate(CertificatePurpose purpose, DateTimeOffset now)
    {
        var name = CertificateLog.Name(purpose);
        var certificates = repository.GetAll(purpose);
        var inUse = _inUse.TryGetValue(purpose, out var set) ? set : [];
        var plan = CertificateRotationPolicy.Plan(purpose, certificates, inUse, now,
            _processStart == default ? now : _processStart, settings.Value.AllowCertificateBootstrap);

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

        foreach (var expired in plan.Delete)
        {
            repository.Delete(expired);
            CertificateLog.CertificateDeleted(logger, name, expired.Thumbprint, CertificateLog.Iso(expired.NotAfter));
        }
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

    private void LogStatus(CertificateStatus status, DateTimeOffset now)
    {
        foreach (var path in SafeLoosePermissions())
        {
            CertificateLog.CertificatePermissionTooOpen(logger, path);
        }

        foreach (var name in SafeUnexpectedPublicFiles())
        {
            CertificateLog.UnexpectedPublicFile(logger, name);
        }

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

            if (CertificateRotationPolicy.IsRestartRequired(purpose, now))
            {
                CertificateLog.RestartRequired(logger, CertificateLog.RestartRequiredLevel(inUseRemaining), name,
                    activeByTime!.Thumbprint, inUse.Thumbprint, CertificateLog.Iso(inUse.NotAfter),
                    CertificateLog.Days(inUseRemaining));
            }

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
