using global::Discord;
using global::Discord.WebSocket;
using Microsoft.Extensions.Options;
using Tatehama.Shukkindai.Server.Configuration;
using Tatehama.Shukkindai.Server.Models;

namespace Tatehama.Shukkindai.Server.Repositories.Discord;

/// <summary>
/// Bot のギルドキャッシュで、メンバーかどうかとユーザー名を調べる。REST にはフォールバックしない。
/// </summary>
public sealed class DiscordRepository : IDiscordRepository
{
    /// <summary>起動時に、キャッシュが信用できる状態になるまで待つ上限。超えたら起動を止める。</summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly DiscordSocketClient _client = new(new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers,
        // GuildPresences の Intent を使わないので、GUILD_CREATE には一部のメンバーしか入らない
        // (大きなギルドではオフラインのメンバーが入らない)。入鋏判定にはキャッシュの完全さが必要なので、全員をダウンロードする。
        // 既知の制約: 全メンバーの取得中にメンバーが抜けると、HasAllMembers が早く true になりうる(1000 人未満のギルドでは起きない)。
        AlwaysDownloadUsers = true,
    });

    private readonly DiscordSettings _settings;
    private readonly ILogger<DiscordRepository> _logger;

    public DiscordRepository(IOptions<DiscordSettings> settings, ILogger<DiscordRepository> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _client.Log += OnDiscordLog;
        // 運転会の最中に Bot がギルドから外された/ギルドが使えなくなったときは、黙って 503 にならないようログに出す
        _client.LeftGuild += guild =>
        {
            if (guild.Id == _settings.GuildId)
            {
                _logger.LogError("Discord bot left guild {GuildId}. Authentication will return temporarily_unavailable.",
                    guild.Id);
            }

            return Task.CompletedTask;
        };
        _client.GuildUnavailable += guild =>
        {
            if (guild.Id == _settings.GuildId)
            {
                _logger.LogWarning("Discord guild {GuildId} became unavailable. Authentication will return temporarily_unavailable until it is back.",
                    guild.Id);
            }

            return Task.CompletedTask;
        };
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

    /// <summary>渡されたギルドの 1 つのオブジェクトで 4 条件を判定する(判定の途中でギルドが差し替わっても、判定が混ざらない)。</summary>
    internal bool IsReady(SocketGuild? guild) =>
        IsSynchronized(_client.ConnectionState, guild is not null, guild?.IsConnected ?? false,
            guild?.HasAllMembers ?? false);

    public bool IsReady() => IsReady(GetGuild());

    public Task<MemberLookupResult> GetMember(ulong memberId)
    {
        // ギルドは 1 回だけ取る。IsReady() と GetUser で別々に取ると、その間に re-identify の READY が処理されて
        // 空の仮ギルド(null ではない。GetUser は常に null)に差し替わり、メンバーを「メンバーでない」と誤判定しうる。
        // 根拠: Discord.Net 3.20.2 DiscordSocketClient.EventHandling.cs:93-127、DiscordSocketClient.cs:338-368。
        var guild = GetGuild();
        if (!IsReady(guild))
        {
            return Task.FromResult(MemberLookupResult.Unavailable);
        }

        var user = guild!.GetUser(memberId);
        if (user is not null)
        {
            return Task.FromResult(MemberLookupResult.Member(user.Username));
        }

        // 見つからなかった。その間に状態が崩れていないことを確かめてから「メンバーでない」と言う
        if (!IsReady(guild) || !ReferenceEquals(GetGuild(), guild))
        {
            return Task.FromResult(MemberLookupResult.Unavailable);
        }

        return Task.FromResult(MemberLookupResult.NotMember);
    }

    /// <summary>
    /// ValidateToken → Login → Start → <see cref="IsReady()"/> が true になるまで待つ(上限は <see cref="StartupTimeout"/>)。
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

    /// <summary>起動失敗の原因を、Gateway に接続できない/ギルドが無い/キャッシュが不完全、に分けて書く。</summary>
    internal string DescribeStartupFailure()
    {
        var guild = GetGuild();
        string reason;
        if (_client.ConnectionState != ConnectionState.Connected || _client.CurrentUser is null)
        {
            // LoginAsync はネットワークに出ないので、無効なトークンや Intent の不足は、Start 以降のこの段階で分かる
            reason = $"could not connect to the Discord Gateway (ConnectionState: {_client.ConnectionState}). " +
                     "Check Discord:BotToken, that the Server Members privileged intent is enabled for the bot, and the network. " +
                     "See the preceding Discord.Net log for details.";
        }
        else if (guild is null)
        {
            reason = $"guild {_settings.GuildId} was not found. The bot is not in the guild, or Discord:GuildId is wrong.";
        }
        else
        {
            reason = $"the guild cache is not complete (IsConnected: {guild.IsConnected}, HasAllMembers: {guild.HasAllMembers}). " +
                     "The guild may be too large to download all members in time.";
        }

        return $"Discord bot did not become ready within {StartupTimeout.TotalSeconds:0} seconds: {reason}";
    }

    internal async Task Logout()
    {
        await _client.StopAsync();
        await _client.LogoutAsync();
        await _client.DisposeAsync();
    }
}
