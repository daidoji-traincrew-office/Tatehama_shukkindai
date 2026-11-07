using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tatehama.Shukkindai.Server.IT.Fixture;

/// <summary>
/// Discord の外部ログインの代わり。Challenge されたら、リクエストヘッダー <see cref="UserIdHeader"/> の
/// Discord ID で Cookie に SignIn し、元の URL へ 302 で戻す。
/// </summary>
public sealed class TestDiscordAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestDiscord";
    public const string UserIdHeader = "X-Test-Discord-Id";
    public const string DenyHeader = "X-Test-Discord-Deny";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // 拒否の再現: 外部ログインには行かず、callback(テスト用 scheme)へ戻り先付きで飛ばす
        if (Request.Headers.TryGetValue(DenyHeader, out var deny))
        {
            Response.Redirect("/auth/callback?state=fake&" + TestDiscordCallbackAuthenticationHandler.ErrorQuery + "=" +
                              Uri.EscapeDataString(deny.ToString()) + "&" +
                              TestDiscordCallbackAuthenticationHandler.RedirectQuery + "=" +
                              Uri.EscapeDataString(properties.RedirectUri!));
            return;
        }

        var id = Request.Headers[UserIdHeader].ToString();
        var identity = new ClaimsIdentity("ExternalLogin");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, id));
        await Context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            properties);
        Response.Redirect(properties.RedirectUri!);
    }
}
