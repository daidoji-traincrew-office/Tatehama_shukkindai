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
using Tatehama.Shukkindai.Server.HostedService;
using Tatehama.Shukkindai.Server.Repositories.Discord;

namespace Tatehama.Shukkindai.Server.IT.Fixture;

public sealed class WebApplicationFixture : WebApplicationFactory<Program>
{
    public const string Issuer = "https://tatehama-shukkindai-test.example/";
    public const string ConnectionString =
        "Host=localhost;Port=5434;Database=postgres;Username=postgres;Password=postgres";

    public string CertificateDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "shukkindai-it-" + Guid.NewGuid());

    /// <summary>Crew と TTC が読む公開鍵(.cer)のディレクトリ。</summary>
    public string PublicDirectory => Path.Combine(CertificateDirectory, "public");

    public FakeDiscordRepository DiscordRepository { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development 以外にして、本番と同じファイル証明書の経路(.cer の書き出しを含む)を通す
        builder.UseEnvironment("IntegrationTest");
        // Program は builder.Configuration を早い段階で読む。環境変数でプロセス全体を汚さないよう UseSetting で渡す
        builder.UseSetting("Authentication:Issuer", Issuer);
        builder.UseSetting("Authentication:CertificateDirectory", CertificateDirectory);
        // 空のディレクトリから鍵を作って起動する(本番では初回のデプロイでだけ true にする)
        builder.UseSetting("Authentication:AllowCertificateBootstrap", "true");
        builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString);
        builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore", "Warning");
        builder.UseSetting("Discord:ClientId", "dummy-client-id");
        builder.UseSetting("Discord:ClientSecret", "dummy-client-secret");
        builder.UseSetting("Discord:BotToken", "dummy-bot-token");
        builder.UseSetting("Discord:GuildId", "1");
        builder.ConfigureTestServices(services =>
        {
            foreach (var descriptor in services
                         .Where(d => d.ImplementationType == typeof(DiscordBotHostedService)
                                     || d.ServiceType == typeof(IDiscordRepository)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<IDiscordRepository>(DiscordRepository);
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

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(CertificateDirectory))
        {
            Directory.Delete(CertificateDirectory, true);
        }
    }
}
