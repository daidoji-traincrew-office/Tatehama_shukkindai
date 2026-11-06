using OpenIddict.Client;
using OpenIddict.Client.WebIntegration;

namespace Tatehama.Shukkindai.Server.Authentication;

/// <summary>
/// Discord が RFC 9207 の iss をリダイレクトに付けるようになったが、OpenIddict の Discord プロバイダーの
/// 静的設定は <c>AuthorizationResponseIssParameterSupported</c> を宣言しておらず、ID2120 で弾かれる。
/// Discord の設定で iss 対応を宣言し、欠落(ID2029)・不一致(ID2119)の検証は上流の
/// <see cref="OpenIddictClientHandlers.ValidateIssuerParameter"/> に任せる。
/// 7.x では registration.Configuration ではなくイベントハンドラ方式でないと効かない。
/// 上流の openiddict-core#2563(8.0)に上げたら、これを削除する。
/// </summary>
public sealed class DiscordIssuerParameterSupport
    : IOpenIddictClientHandler<OpenIddictClientEvents.ProcessAuthenticationContext>
{
    public ValueTask HandleAsync(OpenIddictClientEvents.ProcessAuthenticationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Registration.ProviderType == OpenIddictClientWebIntegrationConstants.ProviderTypes.Discord &&
            context.Configuration is { } configuration)
        {
            // 毎回同じ値を書くだけなので冪等
            configuration.AuthorizationResponseIssParameterSupported = true;
        }

        return default;
    }

    /// <summary>ValidateIssuerParameter の直前に差し込む順序。</summary>
    public static int Order => OpenIddictClientHandlers.ValidateIssuerParameter.Descriptor.Order - 1;

    public static OpenIddictClientBuilder AddDiscordIssuerParameterSupport(OpenIddictClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEventHandler<OpenIddictClientEvents.ProcessAuthenticationContext>(handler => handler
            .AddFilter<OpenIddictClientHandlerFilters.RequireRedirectionRequest>()
            .UseSingletonHandler<DiscordIssuerParameterSupport>()
            .SetOrder(Order)
            .SetType(OpenIddictClientHandlerType.Custom));
    }
}

public static class DiscordIssuerParameterSupportExtensions
{
    public static OpenIddictClientBuilder AddDiscordIssuerParameterSupport(this OpenIddictClientBuilder builder) =>
        DiscordIssuerParameterSupport.AddDiscordIssuerParameterSupport(builder);
}
