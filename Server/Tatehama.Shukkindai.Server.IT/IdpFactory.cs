using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Tatehama.Shukkindai.Server.Authentication;
using Tatehama.Shukkindai.Server.Discord;

namespace Tatehama.Shukkindai.Server.IT;

/// <summary>テストから操作できる <see cref="IDiscordMemberLookup"/>。</summary>
public sealed class FakeDiscordMemberLookup : IDiscordMemberLookup
{
    private readonly Dictionary<ulong, string> _members = new();
    private readonly object _gate = new();

    public bool IsReady { get; set; } = true;

    public void SetMember(ulong id, string username)
    {
        lock (_gate) _members[id] = username;
    }

    public void RemoveMember(ulong id)
    {
        lock (_gate) _members.Remove(id);
    }

    public Task<MemberLookupResult> LookupAsync(ulong discordUserId, CancellationToken cancellationToken)
    {
        if (!IsReady)
        {
            return Task.FromResult(new MemberLookupResult(MemberLookupStatus.Unavailable, null));
        }

        lock (_gate)
        {
            return Task.FromResult(_members.TryGetValue(discordUserId, out var name)
                ? new MemberLookupResult(MemberLookupStatus.Member, name)
                : new MemberLookupResult(MemberLookupStatus.NotMember, null));
        }
    }
}

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

public sealed class IdpFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://tatehama-shukkindai-test.example/";
    public const string ConnectionString =
        "Host=localhost;Port=5434;Database=postgres;Username=postgres;Password=postgres";

    public string CertificateDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "shukkindai-it-" + Guid.NewGuid());

    public FakeDiscordMemberLookup Lookup { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development 以外にして、本番と同じファイル証明書の経路(.cer の書き出しを含む)を通す
        builder.UseEnvironment("IntegrationTest");
        // Program は builder.Configuration を早い段階で読む。環境変数でプロセス全体を汚さないよう UseSetting で渡す
        builder.UseSetting("Authentication:Issuer", Issuer);
        builder.UseSetting("Authentication:CertificateDirectory", CertificateDirectory);
        builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString);
        builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore", "Warning");
        builder.UseSetting("Discord:ClientId", "dummy-client-id");
        builder.UseSetting("Discord:ClientSecret", "dummy-client-secret");
        builder.UseSetting("Discord:BotToken", "dummy-bot-token");
        builder.UseSetting("Discord:GuildId", "1");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DiscordBotHostedService>();
            foreach (var descriptor in services
                         .Where(d => d.ImplementationType == typeof(DiscordBotHostedService)
                                     || d.ServiceType == typeof(IDiscordMemberLookup)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<IDiscordMemberLookup>(Lookup);
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestDiscordAuthenticationHandler>(
                    TestDiscordAuthenticationHandler.SchemeName, _ => { });
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestDiscordCallbackAuthenticationHandler>(
                    TestDiscordCallbackAuthenticationHandler.SchemeName, _ => { });
            services.Configure<ExternalLoginOptions>(o =>
            {
                o.ChallengeScheme = TestDiscordAuthenticationHandler.SchemeName;
                o.CallbackScheme = TestDiscordCallbackAuthenticationHandler.SchemeName;
            });
        });
    }

    public HttpClient CreateIdpClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost/"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(CertificateDirectory))
        {
            Directory.Delete(CertificateDirectory, true);
        }
    }
}
