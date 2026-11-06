using Tatehama.Shukkindai.Server.Authentication;

namespace Tatehama.Shukkindai.Server.UT.Authentication;

public class ExternalLoginFailureTests
{
    [Theory]
    [InlineData("access_denied", "access_denied")]
    [InlineData("temporarily_unavailable", "temporarily_unavailable")]
    [InlineData("server_error", "server_error")]
    [InlineData("invalid_request", "server_error")]
    [InlineData("invalid_token", "server_error")]
    [InlineData("something_unknown", "server_error")]
    [InlineData("", "server_error")]
    [InlineData(null, "server_error")]
    public void MapError_MapsOnlyDenialAndUnavailable(string? discordError, string expected) =>
        Assert.Equal(expected, ExternalLoginFailure.MapError(discordError));

    [Fact]
    public void Description_ForAccessDenied_IsReadableJapanese() =>
        Assert.Equal("Discord での認可が拒否されました。", ExternalLoginFailure.Description("access_denied"));
}
