using Microsoft.AspNetCore;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Client.WebIntegration;
using OpenIddict.Server.AspNetCore;
using Tatehama.Shukkindai.Server.Authentication;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tatehama.Shukkindai.Server.Controllers;

public class AuthorizationController(
    TokenPrincipalService tokenPrincipalService,
    IOptions<ExternalLoginOptions> externalLoginOptions)
    : ControllerBase
{
    [HttpGet("~/connect/authorize"), HttpPost("~/connect/authorize")]
    public async Task<IResult> Authorize(CancellationToken cancellationToken)
    {
        // Discord ログイン後に発行した Cookie を探す。無ければ外部ログインへ Challenge する
        var principal = (await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme))
            .Principal;
        if (principal is null)
        {
            var properties = new AuthenticationProperties { RedirectUri = HttpContext.Request.GetEncodedUrl() };
            return Results.Challenge(properties, [externalLoginOptions.Value.ChallengeScheme]);
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        var request = HttpContext.GetOpenIddictServerRequest();
        if (request is null)
        {
            return Fail(Errors.ServerError, "OpenIddict のリクエストが見つかりませんでした。");
        }

        if (!ulong.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var discordUserId))
        {
            return Fail(Errors.ServerError, "ユーザー ID が見つかりませんでした。");
        }

        var remoteIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await tokenPrincipalService.CreateForAuthorizationAsync(
            discordUserId, request.GetScopes(), remoteIpAddress, cancellationToken);
        return ToResult(result);
    }

    [HttpPost("~/connect/token")]
    public async Task<IResult> Token(CancellationToken cancellationToken)
    {
        var request = HttpContext.GetOpenIddictServerRequest();
        if (request is null)
        {
            return Fail(Errors.ServerError, "OpenIddict のリクエストが見つかりませんでした。");
        }

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            var authenticateResult = await HttpContext.AuthenticateAsync(
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            var principal = authenticateResult.Principal;
            if (principal is null)
            {
                return Fail(Errors.InvalidGrant, "トークンが不正です。");
            }

            if (request.IsAuthorizationCodeGrantType())
            {
                // メンバー確認は authorize で済んでいるので、そのまま SignIn する
                return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            return ToResult(await tokenPrincipalService.CreateForRefreshAsync(principal, cancellationToken));
        }

        return Fail(Errors.UnsupportedGrantType, "このグラントタイプには対応していません。");
    }

    [HttpGet("~/auth/callback"), HttpPost("~/auth/callback")]
    public async Task<IResult> DiscordCallback()
    {
        var result = await HttpContext.AuthenticateAsync(OpenIddictClientWebIntegrationConstants.Providers.Discord);
        if (!result.Succeeded || result.Principal is null)
        {
            return Results.BadRequest();
        }

        // OpenIddict の Discord プロバイダーが userinfo の id から付ける claim だけを使う。
        // ユーザー名は authorize で Bot から取る(ユーザーの Discord トークンでの REST 呼び出しはしない)
        var userId = result.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? result.Principal.FindFirst(Claims.Subject)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.BadRequest();
        }

        var identity = new ClaimsIdentity("ExternalLogin");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId));

        var properties = new AuthenticationProperties(result.Properties.Items)
        {
            RedirectUri = result.Properties.RedirectUri ?? "/",
        };
        return Results.SignIn(new ClaimsPrincipal(identity), properties,
            CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private static IResult ToResult(TokenPrincipalResult result) => result switch
    {
        TokenPrincipalResult.Success success => Results.SignIn(success.Principal, properties: null,
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme),
        TokenPrincipalResult.Rejected rejected => Fail(rejected.Error, rejected.Description),
        _ => throw new InvalidOperationException(),
    };

    private static IResult Fail(string error, string description) => Results.Forbid(
        authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme],
        properties: new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }));
}
