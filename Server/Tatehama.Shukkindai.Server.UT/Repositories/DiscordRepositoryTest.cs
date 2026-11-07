using Discord;
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

    [Fact]
    public void StartupTimeout_Always_IsThirtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), DiscordRepository.StartupTimeout);
    }
}
