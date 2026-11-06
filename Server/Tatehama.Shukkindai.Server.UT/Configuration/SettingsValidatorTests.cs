using Microsoft.Extensions.Hosting;
using Moq;
using Tatehama.Shukkindai.Server.Configuration;

namespace Tatehama.Shukkindai.Server.UT.Configuration;

public class SettingsValidatorTests
{
    private static AuthenticationSettings Valid() => new()
    {
        Issuer = "https://example.test/",
        ClientRedirectPorts = [51180],
    };

    private static bool IsValid(AuthenticationSettings s) =>
        new AuthenticationSettingsValidator().Validate(null, s).Succeeded;

    [Fact]
    public void ValidSettings_Succeed() => Assert.True(IsValid(Valid()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/relative/")]
    [InlineData("tatehama.example/")]
    [InlineData("https://example.test")]
    [InlineData("https://example.test/path")]
    public void InvalidIssuer_Fails(string? issuer)
    {
        var settings = Valid();
        settings.Issuer = issuer;
        Assert.False(IsValid(settings));
    }

    [Fact]
    public void NoPorts_Fails()
    {
        var settings = Valid();
        settings.ClientRedirectPorts = [];
        Assert.False(IsValid(settings));
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public void Discord_RequiredOutsideDevelopment(string environmentName, bool expected)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(environmentName);

        var result = new DiscordSettingsValidator(environment.Object).Validate(null, new DiscordSettings());

        Assert.Equal(expected, result.Succeeded);
    }

    [Fact]
    public void Discord_AllSet_SucceedsInProduction()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Production");

        var result = new DiscordSettingsValidator(environment.Object).Validate(null, new DiscordSettings
        {
            ClientId = "a", ClientSecret = "b", BotToken = "c", GuildId = 1,
        });

        Assert.True(result.Succeeded);
    }
}
