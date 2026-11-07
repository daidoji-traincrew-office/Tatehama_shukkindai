using global::Discord;
using global::Discord.WebSocket;
using Microsoft.Extensions.Options;
using Tatehama.Shukkindai.Server.Configuration;
using Tatehama.Shukkindai.Server.Models;

namespace Tatehama.Shukkindai.Server.Repositories.Discord;

/// <summary>
/// Bot のギルドキャッシュで、メンバーかどうかとユーザー名を調べる。REST にはフォールバックしない。
/// </summary>
public class DiscordRepository : IDiscordRepository
{
    /// <summary>起動時に、キャッシュが信用できる状態になるまで待つ上限。超えたら起動を止める。</summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly DiscordSocketClient _client = new(new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers,
        // 大きなギルドでは、オフラインのメンバーがキャッシュに入らない。入鋏判定にはキャッシュの完全さが必要
        AlwaysDownloadUsers = true,
    });

    private readonly DiscordSettings _settings;
    private readonly ILogger<DiscordRepository> _logger;

    public DiscordRepository(IOptions<DiscordSettings> settings, ILogger<DiscordRepository> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _client.Log += OnDiscordLog;
    }

    private Task OnDiscordLog(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            _ => LogLevel.Trace,
        };
        _logger.Log(level, message.Exception, "Discord.Net [{Source}] {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// キャッシュを信用してよい条件(4つ全部)。1つでも欠ければ、メンバーでないと言い切れない。
    /// <list type="number">
    /// <item>Gateway に接続している。</item>
    /// <item>ギルドが見つかる(Bot が参加していて、GuildId が正しい)。</item>
    /// <item>ギルドが接続中。re-identify の直後は、ギルドが空の仮オブジェクトに差し替わる。
    /// この間は HasAllMembers が true で GetUser が常に null になるため、IsConnected で塞ぐ。</item>
    /// <item>ギルドのメンバー全員を受け取り終えている。大きなギルドで分割して受け取っている途中を塞ぐ。</item>
    /// </list>
    /// </summary>
    internal static bool IsSynchronized(
        ConnectionState connectionState, bool guildFound, bool guildIsConnected, bool guildHasAllMembers) =>
        connectionState == ConnectionState.Connected && guildFound && guildIsConnected && guildHasAllMembers;

    private SocketGuild? GetGuild() => _client.GetGuild(_settings.GuildId);

    public bool IsReady()
    {
        var guild = GetGuild();
        return IsSynchronized(_client.ConnectionState, guild is not null, guild?.IsConnected ?? false,
            guild?.HasAllMembers ?? false);
    }

    public Task<MemberLookupResult> GetMember(ulong memberId)
    {
        if (!IsReady())
        {
            return Task.FromResult(MemberLookupResult.Unavailable);
        }

        // IsReady の直後に切断される可能性があるので、判定に使うギルドは取り直す
        var guild = GetGuild();
        if (guild is null)
        {
            return Task.FromResult(MemberLookupResult.Unavailable);
        }

        var user = guild.GetUser(memberId);
        return Task.FromResult(user is null ? MemberLookupResult.NotMember : MemberLookupResult.Member(user.Username));
    }

    /// <summary>
    /// ValidateToken → Login → Start → <see cref="IsReady"/> が true になるまで待つ(上限は <see cref="StartupTimeout"/>)。
    /// 失敗は例外にして、起動を止める。
    /// </summary>
    internal async Task Initialize(CancellationToken cancellationToken)
    {
        if (_client.LoginState == LoginState.LoggedIn)
        {
            return;
        }

        // 形式不正のトークンは、さっさと落とす
        TokenUtils.ValidateToken(TokenType.Bot, _settings.BotToken);

        await _client.LoginAsync(TokenType.Bot, _settings.BotToken);
        await _client.StartAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StartupTimeout);
        try
        {
            while (!IsReady())
            {
                await Task.Delay(StartupPollInterval, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(DescribeStartupFailure());
        }

        _logger.LogInformation("Discord bot is ready. Guild: {GuildId}", _settings.GuildId);
    }

    private string DescribeStartupFailure()
    {
        var guild = GetGuild();
        var reason = guild is null
            ? $"guild {_settings.GuildId} was not found. The bot is not in the guild, or Discord:GuildId is wrong."
            : $"the guild cache is not complete (ConnectionState: {_client.ConnectionState}, " +
              $"IsConnected: {guild.IsConnected}, HasAllMembers: {guild.HasAllMembers}). " +
              "Check that the GuildMembers privileged intent is enabled for the bot.";
        return $"Discord bot did not become ready within {StartupTimeout.TotalSeconds:0} seconds: {reason}";
    }

    internal async Task Logout()
    {
        await _client.StopAsync();
        await _client.LogoutAsync();
    }
}
