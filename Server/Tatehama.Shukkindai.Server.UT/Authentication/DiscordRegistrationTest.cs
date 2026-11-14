using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Client;
using OpenIddict.Client.WebIntegration;

namespace Tatehama.Shukkindai.Server.UT.Authentication;

/// <summary>
/// OpenIddict 7.8 の Discord プロバイダーは静的設定を持たず、Discord 自身の discovery を読む
/// (discovery は authorization_response_iss_parameter_supported: true を宣言している)。
/// そのため、Crew(OpenIddict 5.x)で必要だった iss パラメーターの回避ハンドラは不要になった。
/// ネットワークには接続せず、その前提だけを固定する。落ちたらアップグレード後の挙動を確認すること。
/// </summary>
public class DiscordRegistrationTest
{
    [Fact]
    public void AddDiscord_NoStaticConfiguration_UsesDiscoveryOfDiscord()
    {
        var services = new ServiceCollection();
        services.AddOpenIddict().AddClient(options =>
        {
            options.AllowAuthorizationCodeFlow();
            options.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
            options.UseWebProviders().AddDiscord(discord => discord
                .SetClientId("id").SetClientSecret("secret").SetRedirectUri("auth/callback"));
        });
        using var provider = services.BuildServiceProvider();

        var registration = provider.GetRequiredService<IOptionsMonitor<OpenIddictClientOptions>>().CurrentValue
            .Registrations.Single(r => r.ProviderName == OpenIddictClientWebIntegrationConstants.Providers.Discord);

        Assert.Null(registration.Configuration);
        Assert.Equal(new("https://discord.com/"), registration.Issuer);
        Assert.Equal(new("https://discord.com/.well-known/openid-configuration"),
            registration.ConfigurationEndpoint);
        Assert.NotNull(registration.ConfigurationManager);
    }
}
