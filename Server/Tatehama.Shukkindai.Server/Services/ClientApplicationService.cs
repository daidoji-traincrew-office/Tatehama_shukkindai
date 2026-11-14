using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Tatehama.Shukkindai.Server.Configuration;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tatehama.Shukkindai.Server.Services;

public interface IClientApplicationService
{
    OpenIddictApplicationDescriptor CreateDescriptor();

    /// <summary>出勤台クライアントを登録する。既に登録済みなら、記述子で上書きする。</summary>
    Task CreateOrUpdate(CancellationToken cancellationToken = default);
}

/// <summary>
/// 出勤台クライアント(public client)を登録する。既に登録済みでも、記述子で必ず上書きする。
/// OpenIddict のマネージャーは Repository に相当するものとして、Service から直接使う。
/// </summary>
public sealed class ClientApplicationService(
    IOpenIddictApplicationManager manager,
    IOptions<AuthenticationSettings> settings) : IClientApplicationService
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
            descriptor.RedirectUris.Add(new($"http://localhost:{port}/"));
        }

        return descriptor;
    }

    public async Task CreateOrUpdate(CancellationToken cancellationToken = default)
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
