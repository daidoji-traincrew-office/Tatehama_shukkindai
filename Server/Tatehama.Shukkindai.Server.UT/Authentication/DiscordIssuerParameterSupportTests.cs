using OpenIddict.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Client;
using OpenIddict.Client.WebIntegration;
using Tatehama.Shukkindai.Server.Authentication;

namespace Tatehama.Shukkindai.Server.UT.Authentication;

/// <summary>
/// Discord の iss 回避ハンドラが前提とする OpenIddict の挙動を固定する。
/// 上流の静的設定の前提が変わった(=上流が修正された)ら落ちる。その場合は回避ハンドラを削除する
/// (openiddict-core#2563、8.0)。
/// </summary>
public class DiscordIssuerParameterSupportTests
{
    private static ServiceProvider BuildProvider(bool withHandler)
    {
        var services = new ServiceCollection();
        services.AddOpenIddict().AddClient(options =>
        {
            options.AllowAuthorizationCodeFlow();
            options.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
            options.UseWebProviders().AddDiscord(discord => discord
                .SetClientId("id").SetClientSecret("secret").SetRedirectUri("auth/callback"));
            if (withHandler)
            {
                options.AddDiscordIssuerParameterSupport();
            }
        });
        return services.BuildServiceProvider();
    }

    private static OpenIddictClientRegistration GetDiscordRegistration(IServiceProvider provider) =>
        provider.GetRequiredService<IOptionsMonitor<OpenIddictClientOptions>>().CurrentValue.Registrations
            .Single(r => r.ProviderName == OpenIddictClientWebIntegrationConstants.Providers.Discord);

    [Fact]
    public async Task UpstreamStaticConfiguration_State_IsPinned()
    {
        // 計画・引き継ぎは「上流(7.x)の Discord 静的設定は iss 対応を宣言していない」としていたが、
        // OpenIddict 7.8.0 では既に宣言している(ハンドラ無しで true)。そのため回避ハンドラは
        // 現状では実質 no-op の保険になっている。この値が変わったら(false に戻ったら)、
        // ハンドラが再び必要になったことを意味するので、ここで気づけるように固定する。
        await using var provider = BuildProvider(withHandler: false);
        var configuration = await GetDiscordRegistration(provider).ConfigurationManager!
            .GetConfigurationAsync(TestContext.Current.CancellationToken);

        Assert.Equal(true, configuration.AuthorizationResponseIssParameterSupported);
    }

    [Fact]
    public async Task Handler_Declares_IssSupport_OnAConfigurationThatDoesNot()
    {
        // 上流の設定が宣言していない場合(別の設定インスタンス)でも、ハンドラが true にする
        var configuration = new OpenIddictConfiguration { AuthorizationResponseIssParameterSupported = false };
        await using var provider = BuildProvider(withHandler: true);
        var context = new OpenIddictClientEvents.ProcessAuthenticationContext(new OpenIddictClientTransaction())
        {
            Registration = GetDiscordRegistration(provider),
            Configuration = configuration,
        };

        await new DiscordIssuerParameterSupport().HandleAsync(context);

        Assert.Equal(true, configuration.AuthorizationResponseIssParameterSupported);
    }

    [Fact]
    public async Task Handler_IsRegisteredBeforeValidateIssuerParameter()
    {
        await using var provider = BuildProvider(withHandler: true);
        var handlers = provider.GetRequiredService<IOptionsMonitor<OpenIddictClientOptions>>().CurrentValue.Handlers
            .Where(h => h.ContextType == typeof(OpenIddictClientEvents.ProcessAuthenticationContext))
            .OrderBy(h => h.Order)
            .Select(h => h.ServiceDescriptor.ImplementationType)
            .ToList();

        var ours = handlers.IndexOf(typeof(DiscordIssuerParameterSupport));
        var upstream = handlers.IndexOf(typeof(OpenIddictClientHandlers.ValidateIssuerParameter));
        Assert.True(ours >= 0, "回避ハンドラが登録されていない");
        Assert.True(upstream >= 0, "上流の ValidateIssuerParameter が見つからない");
        Assert.True(ours < upstream, "回避ハンドラは ValidateIssuerParameter より前に実行されなければならない");
    }

    [Fact]
    public async Task Handler_ResolvesConfigurationBeforeIt_AndDeclaresIssSupportForDiscord()
    {
        await using var provider = BuildProvider(withHandler: true);
        var registration = GetDiscordRegistration(provider);
        var configuration = new OpenIddictConfiguration { AuthorizationResponseIssParameterSupported = false };

        // 設定を解決するハンドラは、回避ハンドラより前に走る
        var handlers = provider.GetRequiredService<IOptionsMonitor<OpenIddictClientOptions>>().CurrentValue.Handlers
            .Where(h => h.ContextType == typeof(OpenIddictClientEvents.ProcessAuthenticationContext))
            .OrderBy(h => h.Order)
            .Select(h => h.ServiceDescriptor.ImplementationType)
            .ToList();
        Assert.True(
            handlers.IndexOf(typeof(OpenIddictClientHandlers.ResolveClientRegistrationFromStateToken)) <
            handlers.IndexOf(typeof(DiscordIssuerParameterSupport)));

        var context = new OpenIddictClientEvents.ProcessAuthenticationContext(new OpenIddictClientTransaction())
        {
            Registration = registration,
            Configuration = configuration,
        };
        await new DiscordIssuerParameterSupport().HandleAsync(context);

        Assert.Equal(true, configuration.AuthorizationResponseIssParameterSupported);
    }

    [Fact]
    public async Task Handler_DoesNothingForOtherProviders()
    {
        await using var provider = BuildProvider(withHandler: true);
        var discord = GetDiscordRegistration(provider);
        var other = new OpenIddictClientRegistration { ProviderType = "Other", Issuer = new Uri("https://example.test/") };
        var configuration = new OpenIddictConfiguration();

        var context = new OpenIddictClientEvents.ProcessAuthenticationContext(new OpenIddictClientTransaction())
        {
            Registration = other,
            Configuration = configuration,
        };
        await new DiscordIssuerParameterSupport().HandleAsync(context);

        Assert.NotEqual(true, configuration.AuthorizationResponseIssParameterSupported);
        Assert.NotNull(discord);
    }
}
