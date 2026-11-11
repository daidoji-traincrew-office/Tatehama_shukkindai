namespace Tatehama.Shukkindai.Client.Core;

public enum AuthFailureKind
{
    /// <summary>Discord サーバーのメンバーではない、または同意画面で拒否された(access_denied)。</summary>
    AccessDenied,

    /// <summary>サーバーが Discord と同期し直している最中(temporarily_unavailable)。</summary>
    TemporarilyUnavailable,

    /// <summary>refresh token が無効・失効、またはメンバーでなくなった(invalid_grant)。</summary>
    InvalidGrant,

    /// <summary>通信失敗やその他のエラー。後で再試行できる。</summary>
    Transient,
}

public abstract record AuthResult
{
    public sealed record Succeeded(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt) : AuthResult;

    public sealed record Failed(AuthFailureKind Kind, string? Error = null, string? Description = null) : AuthResult;
}

public interface IShukkindaiAuthClient
{
    /// <summary>ブラウザでログインする(PKCE)。</summary>
    Task<AuthResult> Login(CancellationToken cancellationToken);

    /// <summary>refresh token でトークンを更新する(rolling。返ってきた refresh token を必ず保存し直すこと)。</summary>
    Task<AuthResult> Refresh(string refreshToken, CancellationToken cancellationToken);

    /// <summary>refresh token を失効させる。失敗は例外ではなく false で返す。</summary>
    Task<bool> Revoke(string refreshToken, CancellationToken cancellationToken);
}
