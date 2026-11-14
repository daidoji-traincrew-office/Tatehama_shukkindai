using Moq;
using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Repositories.Discord;
using Tatehama.Shukkindai.Server.Services;

namespace Tatehama.Shukkindai.Server.UT.Services;

public class DiscordServiceTest
{
    private readonly Mock<IDiscordRepository> _repository = new();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsReady_Always_ReturnsRepositoryState(bool ready)
    {
        _repository.Setup(r => r.IsReady()).Returns(ready);

        Assert.Equal(ready, new DiscordService(_repository.Object).IsReady());
    }

    [Fact]
    public async Task GetMemberById_Member_ReturnsUsername()
    {
        _repository.Setup(r => r.GetMember(1)).ReturnsAsync(MemberLookupResult.Member("alice"));

        var result = await new DiscordService(_repository.Object).GetMemberById(1);

        Assert.Equal(MemberLookupStatus.Member, result.Status);
        Assert.Equal("alice", result.Username);
    }

    [Theory]
    [InlineData(MemberLookupStatus.NotMember)]
    [InlineData(MemberLookupStatus.Unavailable)]
    public async Task GetMemberById_NotMemberOrUnavailable_PassesStatusThrough(MemberLookupStatus status)
    {
        _repository.Setup(r => r.GetMember(2)).ReturnsAsync(new MemberLookupResult(status, null));

        var result = await new DiscordService(_repository.Object).GetMemberById(2);

        Assert.Equal(status, result.Status);
        Assert.Null(result.Username);
    }
}
