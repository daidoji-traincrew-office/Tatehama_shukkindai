using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tatehama.Shukkindai.Server.IT.Fixture;

/// <summary>
/// callback の Authenticate の代わり。OpenIddict 7.8 が Discord のエラー付きの戻りで返す失敗結果
/// (Properties.RedirectUri = challenge 時の戻り先、Items[".error"] = Discord の error)を再現する。
/// 戻り先の query が無い場合は、state token が検証できなかったときと同じく RedirectUri なしで失敗する。
/// </summary>
public sealed class TestDiscordCallbackAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestDiscordCallback";
    public const string ErrorQuery = "error";
    public const string RedirectQuery = "redirect";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var properties = new AuthenticationProperties();
        if (Request.Query.TryGetValue(RedirectQuery, out var redirect))
        {
            properties.RedirectUri = redirect.ToString();
        }

        properties.Items[".error"] = Request.Query[ErrorQuery].ToString();
        return Task.FromResult(AuthenticateResult.Fail("external login failed", properties));
    }
}
