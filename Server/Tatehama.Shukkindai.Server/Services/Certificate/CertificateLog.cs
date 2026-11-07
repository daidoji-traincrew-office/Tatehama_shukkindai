using Tatehama.Shukkindai.Server.Models;

namespace Tatehama.Shukkindai.Server.Services.Certificate;

/// <summary>
/// 証明書まわりの構造化ログ。EventId は固定(Loki などのアラートが数値で絞り込むため、変えない)。
/// 日時のプロパティは、カルチャに依存しないよう "O" 形式の文字列で渡す。
/// </summary>
internal static partial class CertificateLog
{
    public const int CertificateStatusId = 7101;
    public const int CertificateSetStatusId = 7102;
    public const int NextSigningCertificatePendingId = 7103;
    public const int CertificateCreatedId = 7104;
    public const int RestartRequiredId = 7105;
    public const int CertificateDeletedId = 7106;
    public const int CertificateRotationFailedId = 7107;
    public const int CertificatePermissionTooOpenId = 7108;
    public const int StartupCertificatesLoadedId = 7109;
    public const int NoValidSigningCertificateId = 7110;
    public const int PublicCertificatesChangedId = 7111;
    public const int CertificateDirectoryIsRelativeId = 7112;
    public const int UnexpectedPublicFileId = 7113;

    /// <summary>用途の表示名(ログの <c>CertificatePurpose</c>)。ディレクトリ名と同じ。</summary>
    public static string Name(CertificatePurpose purpose) => CertificateRotationPolicy.RuleFor(purpose).DirectoryName;

    /// <summary>日時の文字列(ISO 8601)。</summary>
    public static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>残り日数(切り捨て。期限切れは負の値)。</summary>
    public static int Days(TimeSpan value) => (int)Math.Floor(value.TotalDays);

    /// <summary>1 日 1 回、用途ごとに出す「状態」のログのレベル。人に知らせるべきかで決める。</summary>
    /// <param name="inUseRemaining">プロセスが使っている証明書の NotAfter までの残り。</param>
    /// <param name="successorExists">それより NotAfter が遠い、期限内の証明書がある(再起動すれば、そちらに切り替わる)。</param>
    public static LogLevel SetStatusLevel(TimeSpan inUseRemaining, bool successorExists)
    {
        var days = inUseRemaining.TotalDays;
        if (days > 90)
        {
            return LogLevel.Information;
        }

        if (successorExists)
        {
            // 自動で次の鍵がそろっている。再起動すれば済む話なので、レベルは上げすぎない(再起動の要否は別のログで知らせる)
            return days > 30 ? LogLevel.Information : days > 0 ? LogLevel.Warning : LogLevel.Error;
        }

        // 次の鍵が無い=自動ローテーションが追いついていない。人が対応しないと、再起動したときに起動できなくなる
        return days > 30 ? LogLevel.Warning : LogLevel.Critical;
    }

    /// <summary>再起動が必要なことを知らせるログのレベル。プロセスが使っている証明書の期限が近いほど上げる。</summary>
    public static LogLevel RestartRequiredLevel(TimeSpan inUseRemaining) =>
        inUseRemaining.TotalDays > 30 ? LogLevel.Warning : LogLevel.Error;

    /// <summary>次の署名鍵の公開から切り替えまで。切り替えが近いときは、Crew と TTC の読み込みを確認させる。</summary>
    public static LogLevel NextSigningLevel(TimeSpan untilActivation) =>
        untilActivation.TotalDays <= 14 ? LogLevel.Warning : LogLevel.Information;

    [LoggerMessage(EventId = CertificateStatusId, EventName = "CertificateStatus",
        Message = "Certificate {CertificatePurpose} {CertificateThumbprint} is {CertificateState}; not before {NotBefore}, not after {NotAfter}, {RemainingDays} days left.")]
    public static partial void CertificateStatus(ILogger logger, LogLevel level, string certificatePurpose,
        string certificateThumbprint, string certificateState, string notBefore, string notAfter, int remainingDays);

    [LoggerMessage(EventId = CertificateSetStatusId, EventName = "CertificateSetStatus",
        Message = "Certificate set {CertificatePurpose}: in use {ActiveThumbprint}, usable until {UsableUntil} ({RemainingDays} days left), newest certificate expires at {NewestNotAfter} ({NewestRemainingDays} days left), successor exists: {SuccessorExists}.")]
    public static partial void CertificateSetStatus(ILogger logger, LogLevel level, string certificatePurpose,
        string activeThumbprint, string usableUntil, int remainingDays, string newestNotAfter, int newestRemainingDays,
        bool successorExists);

    [LoggerMessage(EventId = NextSigningCertificatePendingId, EventName = "NextSigningCertificatePending",
        Message = "Next signing certificate {CertificateThumbprint} becomes active at {ActivatesAt} ({DaysUntilActivation} days). It is published as {CerFileName}; Crew and TTC must have loaded it before then.")]
    public static partial void NextSigningCertificatePending(ILogger logger, LogLevel level,
        string certificateThumbprint, string activatesAt, int daysUntilActivation, string cerFileName);

    [LoggerMessage(EventId = CertificateCreatedId, EventName = "CertificateCreated",
        Message = "Created certificate {CertificatePurpose} {CertificateThumbprint}; not before {NotBefore}, not after {NotAfter}.")]
    public static partial void CertificateCreated(ILogger logger, LogLevel level, string certificatePurpose,
        string certificateThumbprint, string notBefore, string notAfter);

    [LoggerMessage(EventId = RestartRequiredId, EventName = "RestartRequired",
        Message = "Restart required: {CertificatePurpose} should now use {CertificateThumbprint}, but this process is using {PreviousThumbprint} (expires at {PreviousNotAfter}, {RemainingDays} days left).")]
    public static partial void RestartRequired(ILogger logger, LogLevel level, string certificatePurpose,
        string certificateThumbprint, string previousThumbprint, string previousNotAfter, int remainingDays);

    [LoggerMessage(EventId = CertificateDeletedId, EventName = "CertificateDeleted", Level = LogLevel.Information,
        Message = "Deleted certificate {CertificatePurpose} {CertificateThumbprint} (not after {NotAfter}).")]
    public static partial void CertificateDeleted(ILogger logger, string certificatePurpose,
        string certificateThumbprint, string notAfter);

    [LoggerMessage(EventId = CertificateRotationFailedId, EventName = "CertificateRotationFailed", Level = LogLevel.Error,
        Message = "Certificate rotation failed for {CertificatePurpose}.")]
    public static partial void CertificateRotationFailed(ILogger logger, Exception exception, string certificatePurpose);

    [LoggerMessage(EventId = CertificatePermissionTooOpenId, EventName = "CertificatePermissionTooOpen", Level = LogLevel.Warning,
        Message = "Certificate path {Path} is readable by group/others. Use mode 0600 for files and 0700 for directories.")]
    public static partial void CertificatePermissionTooOpen(ILogger logger, string path);

    [LoggerMessage(EventId = StartupCertificatesLoadedId, EventName = "StartupCertificatesLoaded", Level = LogLevel.Information,
        Message = "Loaded {CertificateCount} certificates for {CertificatePurpose}; in use {ActiveThumbprint}.")]
    public static partial void StartupCertificatesLoaded(ILogger logger, string certificatePurpose, int certificateCount,
        string activeThumbprint);

    [LoggerMessage(EventId = NoValidSigningCertificateId, EventName = "NoValidSigningCertificate", Level = LogLevel.Critical,
        Message = "No valid server signing certificate (all expired, or all not yet valid). Refusing to start. See Docs/certificate-rotation.md.")]
    public static partial void NoValidSigningCertificate(ILogger logger);

    [LoggerMessage(EventId = PublicCertificatesChangedId, EventName = "PublicCertificatesChanged", Level = LogLevel.Information,
        Message = "Public certificate files changed: {Changes}.")]
    public static partial void PublicCertificatesChanged(ILogger logger, string changes);

    [LoggerMessage(EventId = CertificateDirectoryIsRelativeId, EventName = "CertificateDirectoryIsRelative", Level = LogLevel.Warning,
        Message = "Authentication:CertificateDirectory is a relative path ({ConfiguredPath}); resolved to {ResolvedPath}. Use an absolute path in production so that a different working directory cannot silently point to an empty directory.")]
    public static partial void CertificateDirectoryIsRelative(ILogger logger, string configuredPath, string resolvedPath);

    [LoggerMessage(EventId = UnexpectedPublicFileId, EventName = "UnexpectedPublicFile", Level = LogLevel.Warning,
        Message = "Unexpected file {FileName} in the public certificate directory. Only .cer files belong there; a .pfx contains a private key and must be removed.")]
    public static partial void UnexpectedPublicFile(ILogger logger, string fileName);
}
