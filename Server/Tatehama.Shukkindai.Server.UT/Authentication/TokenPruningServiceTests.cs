using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenIddict.Abstractions;
using Tatehama.Shukkindai.Server.Authentication;

namespace Tatehama.Shukkindai.Server.UT.Authentication;

public class TokenPruningServiceTests
{
    private readonly Mock<IOpenIddictTokenManager> _tokens = new();
    private readonly Mock<IOpenIddictAuthorizationManager> _authorizations = new();

    private TokenPruningService CreateService()
    {
        var provider = new ServiceCollection()
            .AddSingleton(_tokens.Object)
            .AddSingleton(_authorizations.Object)
            .BuildServiceProvider();
        return new TokenPruningService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TokenPruningService>.Instance);
    }

    [Fact]
    public async Task PruneOnce_PrunesTokensAndAuthorizations_OlderThanThreshold()
    {
        DateTimeOffset? tokenThreshold = null, authorizationThreshold = null;
        _tokens.Setup(t => t.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset, CancellationToken>((d, _) => tokenThreshold = d)
            .Returns(new ValueTask<long>(0));
        _authorizations.Setup(a => a.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset, CancellationToken>((d, _) => authorizationThreshold = d)
            .Returns(new ValueTask<long>(0));

        await CreateService().PruneOnceAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(tokenThreshold);
        Assert.NotNull(authorizationThreshold);
        // refresh token の寿命(7日)より長く残す
        Assert.True(DateTimeOffset.UtcNow - tokenThreshold > TimeSpan.FromDays(7));
        Assert.True(DateTimeOffset.UtcNow - authorizationThreshold > TimeSpan.FromDays(7));
    }

    [Fact]
    public async Task PruneOnce_TokenFailure_DoesNotThrow_AndStillPrunesAuthorizations()
    {
        _tokens.Setup(t => t.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("db down"));
        _authorizations.Setup(a => a.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<long>(0));

        await CreateService().PruneOnceAsync(TestContext.Current.CancellationToken);

        _authorizations.Verify(a => a.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
