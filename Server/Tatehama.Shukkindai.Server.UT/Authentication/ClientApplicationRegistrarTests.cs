using Microsoft.Extensions.Options;
using Moq;
using OpenIddict.Abstractions;
using Tatehama.Shukkindai.Server.Authentication;
using Tatehama.Shukkindai.Server.Configuration;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tatehama.Shukkindai.Server.UT.Authentication;

public class ClientApplicationRegistrarTests
{
    private readonly Mock<IOpenIddictApplicationManager> _manager = new();
    private readonly ClientApplicationRegistrar _registrar;

    public ClientApplicationRegistrarTests()
    {
        _registrar = new ClientApplicationRegistrar(_manager.Object, Options.Create(new AuthenticationSettings
        {
            ClientRedirectPorts = [51180, 51181],
        }));
    }

    private static void AssertDescriptor(OpenIddictApplicationDescriptor d)
    {
        Assert.Equal("Tatehama_Shukkindai_Client", d.ClientId);
        Assert.Equal(ClientTypes.Public, d.ClientType);
        Assert.Equal(ApplicationTypes.Native, d.ApplicationType);
        Assert.Equal("出勤台クライアント", d.DisplayName);
        Assert.Null(d.ClientSecret);
        Assert.Equal(
            new[]
            {
                Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
            }.Order(),
            d.Permissions.Order());
        Assert.Equal([Requirements.Features.ProofKeyForCodeExchange], d.Requirements);
        Assert.Equal(new[] { "http://localhost:51180/", "http://localhost:51181/" },
            d.RedirectUris.Select(u => u.AbsoluteUri).Order());
    }

    [Fact]
    public async Task NotRegistered_CreatesWithExpectedDescriptor()
    {
        _manager.Setup(m => m.FindByClientIdAsync(ClientApplicationRegistrar.ClientId, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<object?>((object?)null));
        OpenIddictApplicationDescriptor? created = null;
        _manager.Setup(m => m.CreateAsync(It.IsAny<OpenIddictApplicationDescriptor>(), It.IsAny<CancellationToken>()))
            .Callback<OpenIddictApplicationDescriptor, CancellationToken>((d, _) => created = d)
            .Returns(new ValueTask<object>(new object()));

        await _registrar.EnsureRegisteredAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(created);
        AssertDescriptor(created);
        _manager.Verify(m => m.UpdateAsync(It.IsAny<object>(), It.IsAny<OpenIddictApplicationDescriptor>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AlreadyRegistered_UpdatesWithDescriptor_AndDoesNotCreate()
    {
        var existing = new object();
        _manager.Setup(m => m.FindByClientIdAsync(ClientApplicationRegistrar.ClientId, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<object?>(existing));
        OpenIddictApplicationDescriptor? updated = null;
        _manager.Setup(m => m.UpdateAsync(existing, It.IsAny<OpenIddictApplicationDescriptor>(),
                It.IsAny<CancellationToken>()))
            .Callback<object, OpenIddictApplicationDescriptor, CancellationToken>((_, d, _) => updated = d)
            .Returns(ValueTask.CompletedTask);

        await _registrar.EnsureRegisteredAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(updated);
        AssertDescriptor(updated);
        _manager.Verify(m => m.CreateAsync(It.IsAny<OpenIddictApplicationDescriptor>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
