using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tatehama.Shukkindai.Server.Repositories.Database;
using Tatehama.Shukkindai.Server.Services;

namespace Tatehama.Shukkindai.Server.UT.Services;

public class HealthServiceTest
{
    private readonly Mock<IDatabaseRepository> _database = new();
    private readonly Mock<IDiscordService> _discord = new();

    private HealthService CreateService() =>
        new(_database.Object, _discord.Object, NullLogger<HealthService>.Instance);

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task IsHealthy_DatabaseAndDiscord_HealthyOnlyWhenBothOk(bool databaseOk, bool discordReady,
        bool expected)
    {
        _database.Setup(d => d.CanConnect(It.IsAny<CancellationToken>())).ReturnsAsync(databaseOk);
        _discord.Setup(d => d.IsReady()).Returns(discordReady);

        var actual = await CreateService().IsHealthy(TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task IsHealthy_DatabaseThrows_ReturnsFalse()
    {
        _database.Setup(d => d.CanConnect(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        _discord.Setup(d => d.IsReady()).Returns(true);

        var actual = await CreateService().IsHealthy(TestContext.Current.CancellationToken);

        Assert.False(actual);
    }

    [Fact]
    public async Task IsHealthy_Canceled_Throws()
    {
        _database.Setup(d => d.CanConnect(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateService().IsHealthy(TestContext.Current.CancellationToken));
    }
}
