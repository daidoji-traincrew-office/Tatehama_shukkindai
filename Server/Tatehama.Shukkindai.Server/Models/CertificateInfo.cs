namespace Tatehama.Shukkindai.Server.Models;

/// <summary>証明書の用途。用途ごとにディレクトリ、寿命、ローテーションの方針が違う。</summary>
public enum CertificatePurpose
{
    /// <summary>アクセストークンの署名(JWKS と <c>cert/public/</c> の .cer で公開する)。</summary>
    ServerSigning,

    /// <summary>refresh token と authorization code の暗号化。</summary>
    ServerEncryption,

    /// <summary>Discord ログインの state token の署名。</summary>
    ClientSigning,

    /// <summary>Discord ログインの state token の暗号化。</summary>
    ClientEncryption,
}

public enum CertificateState
{
    /// <summary>署名・暗号化に使われる(有効期間内で、NotAfter が最も遠い)。</summary>
    Active,

    /// <summary>NotBefore が未来。公開はするが、まだ使わない。</summary>
    Pending,

    /// <summary>有効期間内だが、使っていない。</summary>
    Standby,

    /// <summary>NotAfter を過ぎた。</summary>
    Expired,
}

/// <summary>pfx 1 つ分のメタデータ(秘密鍵は持たない)。時刻は UTC。</summary>
public sealed record CertificateInfo(
    CertificatePurpose Purpose,
    string Thumbprint,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string Path);
