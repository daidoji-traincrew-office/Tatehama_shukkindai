using OpenIddict.Client.WebIntegration;

namespace Tatehama.Shukkindai.Server.Authentication;

public sealed class ExternalLoginOptions
{
    /// <summary>authorize で Challenge する scheme。IT でテスト用 scheme に差し替える。</summary>
    public string ChallengeScheme { get; set; } = OpenIddictClientWebIntegrationConstants.Providers.Discord;

    /// <summary>auth/callback で Authenticate する scheme。IT でテスト用 scheme に差し替える。</summary>
    public string CallbackScheme { get; set; } = OpenIddictClientWebIntegrationConstants.Providers.Discord;
}
