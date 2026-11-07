using System.Security.Cryptography.X509Certificates;
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

public class CertificateService(
    ICertificateRepository repository,
    TimeProvider timeProvider,
    ILogger<CertificateService> logger) : ICertificateService
{
    private static readonly CertificatePurpose[] Purposes = Enum.GetValues<CertificatePurpose>();

    private readonly object _gate = new();
    private CertificateSet? _startup;
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

    private CertificateSet LoadStartupCertificates()
    {
        var now = timeProvider.GetUtcNow();
        var failed = !RotateAll(now);

        var all = Purposes.ToDictionary(p => p, repository.GetAll);

        // 動かせない状態は、起動を止める(黙って作り直して起動することはしない)
        if (CertificateRotationPolicy.SelectActive(all[CertificatePurpose.ServerSigning], now) is null)
        {
            CertificateLog.NoValidSigningCertificate(logger);
            throw new InvalidOperationException(
                "有効なサーバー署名証明書がありません(すべて期限切れ、またはすべて NotBefore が未来です)。" +
                "起動を中止します。Docs/certificate-rotation.md の「全部期限切れで起動しないとき」を参照してください。" +
                $" 証明書: {Describe(all[CertificatePurpose.ServerSigning])}");
        }

        if (CertificateRotationPolicy.SelectActive(all[CertificatePurpose.ServerEncryption], now) is null)
        {
            throw new InvalidOperationException(
                "有効なサーバー暗号化証明書がありません。起動を中止します。" +
                "Docs/certificate-rotation.md を参照してください。" +
                (failed ? " 証明書の生成に失敗しています。直前のエラーログを確認してください。" : string.Empty));
        }

        foreach (var purpose in new[] { CertificatePurpose.ClientSigning, CertificatePurpose.ClientEncryption })
        {
            if (all[purpose].Count == 0)
            {
                throw new InvalidOperationException(
                    $"{CertificateLog.Name(purpose)} の証明書がありません。起動を中止します。Docs/certificate-rotation.md を参照してください。");
            }
        }

        var loaded = new Dictionary<CertificatePurpose, IReadOnlyList<X509Certificate2>>();
        foreach (var purpose in Purposes)
        {
            // 署名は、有効なものと、NotBefore が未来の次の鍵。期限切れは載せない。
            // 暗号化は、保持期間内のもの全部(期限切れでも復号には使える)。Client は期限を見ないので全部。
            var targets = purpose == CertificatePurpose.ServerSigning
                ? all[purpose].Where(c => c.NotAfter >= now).ToList()
                : all[purpose].ToList();
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

        _status = BuildStatus(all, failed ? null : now);
        return new CertificateSet(loaded);
    }

    public void Maintain()
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            var success = RotateAll(now);
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

    /// <summary>全用途のローテーションを、ロックの中で 1 回行う。用途ごとの失敗はログに出し、全部成功したときだけ true。</summary>
    private bool RotateAll(DateTimeOffset now)
    {
        try
        {
            return repository.WithLock(() =>
            {
                var success = true;
                foreach (var purpose in Purposes)
                {
                    try
                    {
                        Rotate(purpose, now);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        CertificateLog.CertificateRotationFailed(logger, e, CertificateLog.Name(purpose));
                        success = false;
                    }
                }

                try
                {
                    SyncPublic();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    CertificateLog.CertificateRotationFailed(logger, e, "public");
                    success = false;
                }

                return success;
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // ロックが取れない、cert/ を作れない、など
            CertificateLog.CertificateRotationFailed(logger, e, "all");
            return false;
        }
    }

    private void Rotate(CertificatePurpose purpose, DateTimeOffset now)
    {
        var name = CertificateLog.Name(purpose);
        var certificates = repository.GetAll(purpose);
        var inUse = _inUse.TryGetValue(purpose, out var set) ? set : [];
        var plan = CertificateRotationPolicy.Plan(purpose, certificates, inUse, now);

        if (plan.Create is { } create)
        {
            var created = repository.Create(purpose, create.NotBefore, create.NotAfter);
            // 署名は人(Crew・TTC の運用者)にも関わるので Warning。切り替えまでの猶予が足りないときは Error
            var level = purpose == CertificatePurpose.ServerSigning && !create.Initial
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

    private static string Describe(IReadOnlyList<CertificateInfo> certificates) =>
        certificates.Count == 0
            ? "(なし)"
            : string.Join("; ", certificates.Select(c =>
                $"{c.Thumbprint} notBefore={CertificateLog.Iso(c.NotBefore)} notAfter={CertificateLog.Iso(c.NotAfter)}"));
}
