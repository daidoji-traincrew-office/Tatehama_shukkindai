using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OpenIddict.Abstractions;
using Tatehama.Shukkindai.Server.Services;

namespace Tatehama.Shukkindai.Server.UT.Services;

public class TokenPruningServiceTest
{
    private readonly Mock<IOpenIddictTokenManager> _tokens = new();
    private readonly Mock<IOpenIddictAuthorizationManager> _authorizations = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero));

    private TokenPruningService CreateService() => new(_tokens.Object, _authorizations.Object, _time,
        NullLogger<TokenPruningService>.Instance);

    [Fact]
    public async Task Prune_Always_PrunesTokensAndAuthorizationsOlderThanThreshold()
    {
        // Arrange
        DateTimeOffset? tokenThreshold = null, authorizationThreshold = null;
        _tokens.Setup(t => t.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset, CancellationToken>((d, _) => tokenThreshold = d)
            .Returns(new ValueTask<long>(0));
        _authorizations.Setup(a => a.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset, CancellationToken>((d, _) => authorizationThreshold = d)
            .Returns(new ValueTask<long>(0));

        // Act
        await CreateService().Prune(TestContext.Current.CancellationToken);

        // Assert
        var expected = _time.GetUtcNow() - TokenPruningService.Threshold;
        Assert.Equal(expected, tokenThreshold);
        Assert.Equal(expected, authorizationThreshold);
        // refresh token の寿命(7日)より長く残す
        Assert.True(TokenPruningService.Threshold > TimeSpan.FromDays(7));
    }

    [Fact]
    public async Task Prune_TokenPruneFails_DoesNotThrowAndStillPrunesAuthorizations()
    {
        // Arrange
        _tokens.Setup(t => t.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("db down"));
        _authorizations.Setup(a => a.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<long>(0));

        // Act
        await CreateService().Prune(TestContext.Current.CancellationToken);

        // Assert
        _authorizations.Verify(a => a.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
