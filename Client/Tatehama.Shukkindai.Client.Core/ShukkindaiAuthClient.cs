using Microsoft.Extensions.Logging;
using OpenIddict.Client;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Abstractions.OpenIddictExceptions;

namespace Tatehama.Shukkindai.Client.Core;

/// <summary>
/// OpenIddict.Client で出勤台 IdP にログイン・更新・失効する。プロトコル(PKCE・state・iss・discovery)は OpenIddict に任せる。
/// </summary>
public sealed class ShukkindaiAuthClient(
    OpenIddictClientService service,
    Uri issuer,
    ILogger<ShukkindaiAuthClient> logger) : IShukkindaiAuthClient
{
    public async Task<AuthResult> Login(CancellationToken cancellationToken)
    {
        try
        {
            var challenge = await service.ChallengeInteractivelyAsync(new()
            {
                Issuer = issuer,
                CancellationToken = cancellationToken,
            });
            var result = await service.AuthenticateInteractivelyAsync(new()
            {
                Nonce = challenge.Nonce,
                CancellationToken = cancellationToken,
            });
            return ToResult(
                result.BackchannelAccessToken,
                result.BackchannelAccessTokenExpirationDate,
                result.RefreshToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "出勤台とのやり取りに失敗した。");
            return ToFailure(e);
        }
    }

    public async Task<AuthResult> Refresh(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.AuthenticateWithRefreshTokenAsync(new()
            {
                Issuer = issuer,
                RefreshToken = refreshToken,
                DisableUserInfo = true,
                CancellationToken = cancellationToken,
            });
            return ToResult(
                result.AccessToken,
                result.AccessTokenExpirationDate,
                result.RefreshToken ?? refreshToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "出勤台とのやり取りに失敗した。");
            return ToFailure(e);
        }
    }

    public async Task<bool> Revoke(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            await service.RevokeTokenAsync(new()
            {
                Issuer = issuer,
                Token = refreshToken,
                TokenTypeHint = TokenTypeHints.RefreshToken,
                CancellationToken = cancellationToken,
            });
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "refresh token の revoke に失敗した。");
            return false;
        }
    }

    private static AuthResult ToResult(string? accessToken, DateTimeOffset? expirationDate, string? refreshToken)
    {
        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
        {
            return new AuthResult.Failed(AuthFailureKind.Transient, Description: "サーバーの応答にトークンが含まれていない。");
        }

        var expiresAt = expirationDate ?? AccessTokenClaims.Parse(accessToken)?.ExpiresAt;
        return expiresAt is null
            ? new AuthResult.Failed(AuthFailureKind.Transient, Description: "アクセストークンの期限が分からない。")
            : new AuthResult.Succeeded(accessToken, refreshToken, expiresAt.Value);
    }

    /// <summary>authentication.md のエラー表に合わせる。error で分類し、それ以外(通信失敗など)は Transient。</summary>
    public static AuthResult.Failed ToFailure(Exception exception)
    {
        if (exception is not ProtocolException protocol)
        {
            return new(AuthFailureKind.Transient, Description: exception.GetType().Name);
        }

        var kind = protocol.Error switch
        {
            Errors.AccessDenied => AuthFailureKind.AccessDenied,
            Errors.TemporarilyUnavailable => AuthFailureKind.TemporarilyUnavailable,
            Errors.InvalidGrant => AuthFailureKind.InvalidGrant,
            _ => AuthFailureKind.Transient,
        };
        return new(kind, protocol.Error, protocol.ErrorDescription);
    }
}
