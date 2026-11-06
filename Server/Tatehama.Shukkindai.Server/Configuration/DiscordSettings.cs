using Microsoft.Extensions.Options;

namespace Tatehama.Shukkindai.Server.Configuration;

/// <summary>
/// 設定セクション <c>Discord</c>。秘密情報は User Secrets か環境変数で渡す。
/// ローカルでも必須(User Secrets に入れる)。
/// </summary>
public sealed class DiscordSettings
{
    public const string SectionName = "Discord";

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? BotToken { get; set; }
    public ulong GuildId { get; set; }
}

public sealed class DiscordSettingsValidator : IValidateOptions<DiscordSettings>
{
    public ValidateOptionsResult Validate(string? name, DiscordSettings options)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ClientId)) errors.Add("Discord:ClientId は必須です。");
        if (string.IsNullOrWhiteSpace(options.ClientSecret)) errors.Add("Discord:ClientSecret は必須です。");
        if (string.IsNullOrWhiteSpace(options.BotToken)) errors.Add("Discord:BotToken は必須です。");
        if (options.GuildId == 0) errors.Add("Discord:GuildId は必須です(ulong)。");
        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
