using System.Security.Cryptography.X509Certificates;

namespace Tatehama.Shukkindai.Server.Models;

/// <summary>
/// 1 つの用途の、ある時点の状態。<see cref="InUseThumbprints"/> は、このプロセスが起動時に OpenIddict へ登録した証明書
/// (再起動するまで入れ替わらない)。<see cref="Certificates"/> は、ディスク上の最新の一覧。
/// </summary>
public sealed record PurposeStatus(
    CertificatePurpose Purpose,
    IReadOnlyList<CertificateInfo> Certificates,
    IReadOnlySet<string> InUseThumbprints,
    string? InUseActiveThumbprint)
{
    /// <summary>このプロセスが実際に使っている証明書。</summary>
    public CertificateInfo? InUseActive => InUseActiveThumbprint is null
        ? null
        : Certificates.FirstOrDefault(c => c.Thumbprint == InUseActiveThumbprint);
}

public sealed record CertificateStatus(
    IReadOnlyList<PurposeStatus> Purposes,
    DateTimeOffset? LastRotationSuccess)
{
    public static CertificateStatus Empty { get; } = new([], null);
}

/// <summary>起動時に OpenIddict へ登録する証明書(秘密鍵つき)。プロセスの間、保持する。</summary>
public sealed class CertificateSet(Dictionary<CertificatePurpose, IReadOnlyList<X509Certificate2>> byPurpose)
{
    public IReadOnlyList<X509Certificate2> Get(CertificatePurpose purpose) =>
        byPurpose.TryGetValue(purpose, out var list) ? list : [];
}
