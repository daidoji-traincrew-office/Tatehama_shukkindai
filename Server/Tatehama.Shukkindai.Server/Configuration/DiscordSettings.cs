using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Tatehama.Shukkindai.Server.Configuration;

/// <summary>設定セクション <c>Discord</c>。秘密情報は User Secrets か環境変数で渡す。</summary>
public sealed class DiscordSettings
{
    public const string SectionName = "Discord";

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? BotToken { get; set; }
    public ulong GuildId { get; set; }
}

/// <summary>Development 以外では、すべて必須。</summary>
public sealed class DiscordSettingsValidator(IHostEnvironment environment) : IValidateOptions<DiscordSettings>
{
    public ValidateOptionsResult Validate(string? name, DiscordSettings options)
    {
        if (environment.IsDevelopment())
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ClientId)) errors.Add("Discord:ClientId は必須です。");
        if (string.IsNullOrWhiteSpace(options.ClientSecret)) errors.Add("Discord:ClientSecret は必須です。");
        if (string.IsNullOrWhiteSpace(options.BotToken)) errors.Add("Discord:BotToken は必須です。");
        if (options.GuildId == 0) errors.Add("Discord:GuildId は必須です。");
        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
