using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Tatehama.Shukkindai.Server.Configuration;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tatehama.Shukkindai.Server.Authentication;

/// <summary>出勤台クライアント(public client)を登録する。既に登録済みでも、記述子で必ず上書きする。</summary>
public sealed class ClientApplicationRegistrar(
    IOpenIddictApplicationManager manager,
    IOptions<AuthenticationSettings> settings)
{
    public const string ClientId = "Tatehama_Shukkindai_Client";

    public OpenIddictApplicationDescriptor CreateDescriptor()
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = ClientId,
            ClientType = ClientTypes.Public,
            ApplicationType = ApplicationTypes.Native,
            DisplayName = "出勤台クライアント",
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
            },
            Requirements =
            {
                Requirements.Features.ProofKeyForCodeExchange,
            },
        };
        foreach (var port in settings.Value.ClientRedirectPorts)
        {
            descriptor.RedirectUris.Add(new Uri($"http://localhost:{port}/"));
        }

        return descriptor;
    }

    public async Task EnsureRegisteredAsync(CancellationToken cancellationToken = default)
    {
        var descriptor = CreateDescriptor();
        var application = await manager.FindByClientIdAsync(ClientId, cancellationToken);
        if (application is null)
        {
            await manager.CreateAsync(descriptor, cancellationToken);
        }
        else
        {
            await manager.UpdateAsync(application, descriptor, cancellationToken);
        }
    }
}
