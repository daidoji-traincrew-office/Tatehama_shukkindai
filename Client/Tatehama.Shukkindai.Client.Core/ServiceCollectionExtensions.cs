using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Client;
using Tatehama.Shukkindai.TokenFile;

namespace Tatehama.Shukkindai.Client.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 出勤台クライアントの認証一式を登録する。<see cref="ITokenProtector"/> は呼び出し側が登録する
    /// (Windows では <see cref="DpapiTokenProtector"/>)。
    /// </summary>
    public static IServiceCollection AddShukkindaiClient(
        this IServiceCollection services,
        IConfiguration configuration,
        string issuerAddress)
    {
        services.Configure<ShukkindaiClientOptions>(configuration.GetSection(ShukkindaiClientOptions.SectionName));
        var settings = configuration.GetSection(ShukkindaiClientOptions.SectionName).Get<ShukkindaiClientOptions>()
                       ?? new ShukkindaiClientOptions();
        if (!Uri.TryCreate(issuerAddress, UriKind.Absolute, out var issuer))
        {
            throw new InvalidOperationException(
                "出勤台のissuerが設定されていません。信号メーカーにお問い合わせください。");
        }

        services.AddOpenIddict()
            .AddClient(builder =>
            {
                builder.AllowAuthorizationCodeFlow()
                    .AllowRefreshTokenFlow();
                // ログイン途中の状態(state トークン)は、同じプロセスの中だけで受け渡す。DB(OpenIddict.Core)は使わない
                builder.DisableTokenStorage();
                builder.AddEphemeralEncryptionKey()
                    .AddEphemeralSigningKey();
                builder.UseSystemIntegration(integration => integration
                    .EnableEmbeddedWebServer()
                    .SetAllowedEmbeddedWebServerPorts(settings.RedirectPort));
                builder.UseSystemNetHttp();
                // openid は付けない(サーバーの discovery に openid が無いので OpenIddict も付けない。id_token は発行されない)
                builder.AddRegistration(new()
                {
                    Issuer = issuer,
                    ClientId = ShukkindaiClientOptions.ClientId,
                    RedirectUri = new($"http://localhost:{settings.RedirectPort}/"),
                    Scopes = { "offline_access" },
                });
            });

        services.AddSingleton<IShukkindaiAuthClient>(sp => new ShukkindaiAuthClient(
            sp.GetRequiredService<OpenIddictClientService>(), issuer,
            sp.GetRequiredService<ILogger<ShukkindaiAuthClient>>()));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new RefreshTokenStore(
            sp.GetRequiredService<ITokenProtector>(), TokenFilePaths.RefreshToken));
        services.AddSingleton(sp => new AccessTokenFileWriter(
            sp.GetRequiredService<ITokenProtector>(), TokenFilePaths.AccessToken));
        services.AddSingleton<SessionManager>();
        return services;
    }
}
