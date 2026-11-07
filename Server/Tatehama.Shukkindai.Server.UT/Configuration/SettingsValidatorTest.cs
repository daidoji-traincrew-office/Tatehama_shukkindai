using Tatehama.Shukkindai.Server.Configuration;

namespace Tatehama.Shukkindai.Server.UT.Configuration;

public class SettingsValidatorTest
{
    private static AuthenticationSettings Valid() => new()
    {
        Issuer = "https://example.test/",
        ClientRedirectPorts = [51180],
    };

    private static bool IsValid(AuthenticationSettings s) =>
        new AuthenticationSettingsValidator().Validate(null, s).Succeeded;

    [Fact]
    public void Validate_ValidSettings_Succeeds() => Assert.True(IsValid(Valid()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/relative/")]
    [InlineData("tatehama.example/")]
    [InlineData("http://example.test/")]
    [InlineData("https://example.test")]
    [InlineData("https://example.test/path")]
    public void Validate_InvalidIssuer_Fails(string? issuer)
    {
        var settings = Valid();
        settings.Issuer = issuer;
        Assert.False(IsValid(settings));
    }

    [Fact]
    public void Validate_NoPorts_Fails()
    {
        var settings = Valid();
        settings.ClientRedirectPorts = [];
        Assert.False(IsValid(settings));
    }

    [Fact]
    public void Validate_DiscordSettingMissingEvenInDevelopment_Fails()
    {
        var result = new DiscordSettingsValidator().Validate(null, new DiscordSettings());

        Assert.True(result.Failed);
        Assert.Contains("Discord:BotToken は必須です。", result.Failures!);
        Assert.Contains("Discord:ClientId は必須です。", result.Failures!);
        Assert.Contains("Discord:ClientSecret は必須です。", result.Failures!);
        Assert.Contains(result.Failures!, f => f.StartsWith("Discord:GuildId"));
    }

    [Fact]
    public void Validate_DiscordAllSet_Succeeds()
    {
        var result = new DiscordSettingsValidator().Validate(null, new DiscordSettings
        {
            ClientId = "a",
            ClientSecret = "b",
            BotToken = "c",
            GuildId = 1,
        });

        Assert.True(result.Succeeded);
    }
}
