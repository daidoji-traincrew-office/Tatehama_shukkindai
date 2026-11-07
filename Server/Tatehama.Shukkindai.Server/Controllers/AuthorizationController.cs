using Microsoft.AspNetCore;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Client.AspNetCore;
using OpenIddict.Server.AspNetCore;
using Tatehama.Shukkindai.Server.Authentication;
using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Services;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tatehama.Shukkindai.Server.Controllers;

public class AuthorizationController(
    ITokenService tokenService,
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
            // POST の場合も、フォームのパラメーターを失わないように戻り先を組み立てる(OpenIddict のサンプルと同じ)
            var parameters = Request.HasFormContentType ? Request.Form.ToList() : Request.Query.ToList();
            var properties = new AuthenticationProperties
            {
                RedirectUri = Request.PathBase + Request.Path + QueryString.Create(parameters),
            };
            return Results.Challenge(properties, [externalLoginOptions.Value.ChallengeScheme]);
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        var request = HttpContext.GetOpenIddictServerRequest();
        if (request is null)
        {
            return Fail(Errors.ServerError, "OpenIddict のリクエストが見つかりませんでした。");
        }

        // Discord での認可が失敗していた場合(callback が印を付けた Cookie)は、ここでクライアントへエラーを返す。
        // クライアントへの error 応答を正しい state 付きで作れるのは authorize エンドポイントだけ
        var externalError = principal.FindFirst(ExternalLoginFailure.ClaimType)?.Value;
        if (externalError is not null)
        {
            return Fail(externalError, ExternalLoginFailure.Description(externalError));
        }

        if (!ulong.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var discordUserId))
        {
            return Fail(Errors.ServerError, "ユーザー ID が見つかりませんでした。");
        }

        var remoteIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await tokenService.CreatePrincipalForAuthorization(
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

            return ToResult(await tokenService.CreatePrincipalForRefresh(principal, cancellationToken));
        }

        return Fail(Errors.UnsupportedGrantType, "このグラントタイプには対応していません。");
    }

    [HttpGet("~/auth/callback"), HttpPost("~/auth/callback")]
    public async Task<IResult> DiscordCallback()
    {
        var result = await HttpContext.AuthenticateAsync(externalLoginOptions.Value.CallbackScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            // OpenIddict は、state token の検証に成功していれば、challenge 時の RedirectUri(元の authorize の URL)を
            // 失敗結果の Properties に入れる。取れたときは、ログイン途中の Cookie に失敗の印を付けて authorize に戻す。
            // (callback からクライアントの redirect_uri へ直接は戻さない。state を付けられず、open redirect にもなるため)
            var returnUri = result.Properties?.RedirectUri;
            if (!string.IsNullOrEmpty(returnUri) && Url.IsLocalUrl(returnUri))
            {
                result.Properties!.Items.TryGetValue(OpenIddictClientAspNetCoreConstants.Properties.Error,
                    out var discordError);
                var failureIdentity = new ClaimsIdentity("ExternalLoginFailed");
                failureIdentity.AddClaim(new Claim(ExternalLoginFailure.ClaimType,
                    ExternalLoginFailure.MapError(discordError)));
                return await SignInAndRedirect(new ClaimsPrincipal(failureIdentity), returnUri);
            }

            // 戻り先が分からない(state が無い・壊れている・期限切れなど)場合は、サーバー側でエラーを表示する
            return Results.Text(
                "Discord でのログインを完了できませんでした。出勤台クライアントからやり直してください。",
                "text/plain; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);
        }

        // OpenIddict の Discord プロバイダーが userinfo の id から付ける claim だけを使う。
        // ユーザー名は authorize で Bot から取る(ユーザーの Discord トークンでの REST 呼び出しはしない)
        var userId = result.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? result.Principal.FindFirst(Claims.Subject)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Text("Discord のユーザー ID を取得できませんでした。",
                "text/plain; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);
        }

        var identity = new ClaimsIdentity("ExternalLogin");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId));

        // Discord のトークンなどを Cookie に持ち回らない。戻り先(元の authorize の URL)だけ使う
        var successReturnUri = result.Properties.RedirectUri;
        return await SignInAndRedirect(new ClaimsPrincipal(identity),
            !string.IsNullOrEmpty(successReturnUri) && Url.IsLocalUrl(successReturnUri) ? successReturnUri : "/");
    }

    /// <summary>ログイン途中の Cookie を発行し、(ローカルの)戻り先へ 302 で戻す。Cookie ハンドラは自動では戻さない。</summary>
    private async Task<IResult> SignInAndRedirect(ClaimsPrincipal principal, string localReturnUri)
    {
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return Results.LocalRedirect(localReturnUri);
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
