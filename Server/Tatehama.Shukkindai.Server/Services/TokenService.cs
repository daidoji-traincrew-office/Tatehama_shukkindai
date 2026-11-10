using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using Tatehama.Shukkindai.Server.Authentication;
using Tatehama.Shukkindai.Server.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tatehama.Shukkindai.Server.Services;

public interface ITokenService
{
    Task<TokenPrincipalResult> CreatePrincipalForAuthorization(
        ulong discordUserId, IEnumerable<string> requestedScopes, string remoteIpAddress,
        CancellationToken cancellationToken);

    /// <param name="refreshTokenPrincipal">OpenIddict が検証した refresh token の principal。<c>sub</c> から Discord のユーザーを引く。</param>
    /// <param name="cancellationToken">今は使っていない(メンバー確認が同期のキャッシュ参照のため)。Discord への問い合わせや DB が入ったときに署名を変えないよう、API として残す。</param>
    Task<TokenPrincipalResult> CreatePrincipalForRefresh(
        ClaimsPrincipal refreshTokenPrincipal, CancellationToken cancellationToken);
}

/// <summary>トークンに載せる principal を作る。判断はここに集め、コントローラーは HTTP の糊に徹する。</summary>
public sealed class TokenService(
    IDiscordService discordService,
    IUserLoginObserver observer,
    ILogger<TokenService> logger) : ITokenService
{
    private const string UnavailableDescription =
        "Discord との連携の準備ができていません。しばらくしてから再試行してください。";

    public async Task<TokenPrincipalResult> CreatePrincipalForAuthorization(
        ulong discordUserId, IEnumerable<string> requestedScopes, string remoteIpAddress,
        CancellationToken cancellationToken)
    {
        var result = await discordService.GetMemberById(discordUserId);
        switch (result.Status)
        {
            case MemberLookupStatus.NotMember:
                return new TokenPrincipalResult.Rejected(Errors.AccessDenied,
                    "Discord サーバーのメンバーではありません。");
            case MemberLookupStatus.Unavailable:
                return new TokenPrincipalResult.Rejected(Errors.TemporarilyUnavailable, UnavailableDescription);
        }

        var username = result.Username
                       ?? throw new InvalidOperationException("Member の結果には Username が必要です。");
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType,
            Claims.Name, Claims.Role);
        identity.AddClaim(new(Claims.Subject, discordUserId.ToString()));
        identity.AddClaim(new(Claims.Name, username));

        // offline_access 以外の scope は通さない
        // (大文字小文字を無視して比べ、正規の表記で返す)
        identity.SetScopes(((string[])[Scopes.OfflineAccess]).Intersect(requestedScopes, StringComparer.OrdinalIgnoreCase));
        TokenDestinations.Apply(identity);

        await observer.OnUserLoggedIn(discordUserId, username, cancellationToken);
        logger.LogInformation(
            "User logged in. RemoteIpAddress: {RemoteIpAddress}, DiscordMemberName: {DiscordMemberName}, UserId: {UserId}",
            remoteIpAddress, username, discordUserId);

        return new TokenPrincipalResult.Success(new(identity));
    }

    public async Task<TokenPrincipalResult> CreatePrincipalForRefresh(
        ClaimsPrincipal refreshTokenPrincipal, CancellationToken cancellationToken)
    {
        if (!ulong.TryParse(refreshTokenPrincipal.FindFirst(Claims.Subject)?.Value, out var discordUserId))
        {
            return new TokenPrincipalResult.Rejected(Errors.InvalidGrant, "refresh token が不正です。");
        }

        var result = await discordService.GetMemberById(discordUserId);
        switch (result.Status)
        {
            case MemberLookupStatus.NotMember:
                logger.LogInformation("Refresh rejected: user is no longer a guild member. UserId: {UserId}",
                    discordUserId);
                return new TokenPrincipalResult.Rejected(Errors.InvalidGrant,
                    "Discord サーバーのメンバーではなくなりました。");
            case MemberLookupStatus.Unavailable:
                return new TokenPrincipalResult.Rejected(Errors.TemporarilyUnavailable, UnavailableDescription);
        }

        // OpenIddict のサンプル(Velusia)と同じく、元の claim(scope を含む)を引き継ぐ
        var identity = new ClaimsIdentity(refreshTokenPrincipal.Claims,
            TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Name, result.Username
                                       ?? throw new InvalidOperationException("Member の結果には Username が必要です。"));
        TokenDestinations.Apply(identity);

        return new TokenPrincipalResult.Success(new(identity));
    }
}
