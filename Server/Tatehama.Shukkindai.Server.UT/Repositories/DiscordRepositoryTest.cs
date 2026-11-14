using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tatehama.Shukkindai.Server.Configuration;
using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Repositories.Discord;

namespace Tatehama.Shukkindai.Server.UT.Repositories;

public class DiscordRepositoryTest
{
    [Theory]
    // 4条件が全部そろったときだけ信用する
    [InlineData(ConnectionState.Connected, true, true, true, true)]
    // 1. 接続していない(切断中・接続中・切断処理中)
    [InlineData(ConnectionState.Disconnected, true, true, true, false)]
    [InlineData(ConnectionState.Connecting, true, true, true, false)]
    [InlineData(ConnectionState.Disconnecting, true, true, true, false)]
    // 2. ギルドが見つからない
    [InlineData(ConnectionState.Connected, false, false, false, false)]
    // 3. re-identify 直後の仮オブジェクト(HasAllMembers が true でも信用しない)
    [InlineData(ConnectionState.Connected, true, false, true, false)]
    // 4. メンバーを受け取っている途中
    [InlineData(ConnectionState.Connected, true, true, false, false)]
    public void IsSynchronized_Conditions_TrustsCacheOnlyWhenAllHold(
        ConnectionState connectionState, bool guildFound, bool guildIsConnected, bool guildHasAllMembers,
        bool expected)
    {
        var actual = DiscordRepository.IsSynchronized(connectionState, guildFound, guildIsConnected,
            guildHasAllMembers);

        Assert.Equal(expected, actual);
    }

    // SocketGuild(IsConnected / HasAllMembers)の状態は、外から作れないので、ここでは再現できない。
    // 4 条件の根拠は Discord.Net 3.20.2 のソース: DiscordSocketClient.EventHandling.cs:93-127(re-identify の READY で
    // ギルドが仮オブジェクトに差し替わる)、SocketGuild.cs:163・431-491(IsConnected / HasAllMembers)、
    // DiscordSocketClient.cs:338-368・1060-1078。実機での確認は手順書にある。

    private static DiscordRepository CreateRepository(string botToken = "dummy") =>
        new(Options.Create(new DiscordSettings { ClientId = "id", ClientSecret = "secret", BotToken = botToken, GuildId = 1 }),
            NullLogger<DiscordRepository>.Instance);

    [Fact]
    public async Task GetMember_NotLoggedIn_IsUnavailable()
    {
        var repository = CreateRepository();

        Assert.False(repository.IsReady());
        Assert.False(repository.IsReady(null));
        var result = await repository.GetMember(123);

        Assert.Equal(MemberLookupStatus.Unavailable, result.Status);
        Assert.Null(result.Username);
    }

    [Fact]
    public async Task Initialize_MalformedBotToken_ThrowsArgumentException()
    {
        var repository = CreateRepository("not-a-token");

        await Assert.ThrowsAsync<ArgumentException>(() => repository.Initialize(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DescribeStartupFailure_NotConnected_BlamesGatewayConnectionNotGuild()
    {
        var message = CreateRepository().DescribeStartupFailure();

        Assert.Contains("could not connect to the Discord Gateway", message);
        Assert.Contains("BotToken", message);
        Assert.Contains("Server Members", message);
        Assert.DoesNotContain("was not found", message);
    }
}
