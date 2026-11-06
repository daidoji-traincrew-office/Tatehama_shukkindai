using global::Discord;
using global::Discord.Net;
using global::Discord.WebSocket;
using Microsoft.Extensions.Options;
using Tatehama.Shukkindai.Server.Configuration;

namespace Tatehama.Shukkindai.Server.Discord;

/// <summary>Bot で、現在の Discord サーバーのメンバーかどうかとユーザー名を調べる。</summary>
public sealed class DiscordMemberLookup : IDiscordMemberLookup
{
    private readonly DiscordSocketClient _client = new(new DiscordSocketConfig
    {
        GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMembers,
    });

    private readonly DiscordSettings _settings;
    private readonly ILogger<DiscordMemberLookup> _logger;
    private volatile bool _readyReceived;

    public DiscordMemberLookup(IOptions<DiscordSettings> settings, ILogger<DiscordMemberLookup> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _client.Ready += () =>
        {
            _readyReceived = true;
            return Task.CompletedTask;
        };
    }

    public bool IsReady => _readyReceived && _client.GetGuild(_settings.GuildId) is not null;

    public async Task<MemberLookupResult> LookupAsync(ulong discordUserId, CancellationToken cancellationToken)
    {
        if (!IsReady)
        {
            return new MemberLookupResult(MemberLookupStatus.Unavailable, null);
        }

        // 1. ギルドのキャッシュ
        var guild = _client.GetGuild(_settings.GuildId);
        var cached = guild?.GetUser(discordUserId);
        if (cached is not null)
        {
            return new MemberLookupResult(MemberLookupStatus.Member, cached.Username);
        }

        // 2. キャッシュに無くても非メンバーとは限らないので REST で確かめる
        try
        {
            var user = await _client.Rest.GetGuildUserAsync(_settings.GuildId, discordUserId,
                new RequestOptions { CancelToken = cancellationToken });
            return user is null
                ? new MemberLookupResult(MemberLookupStatus.NotMember, null)
                : new MemberLookupResult(MemberLookupStatus.Member, user.Username);
        }
        catch (HttpException e) when (e.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            return new MemberLookupResult(MemberLookupStatus.NotMember, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Failed to look up Discord member. UserId: {UserId}", discordUserId);
            return new MemberLookupResult(MemberLookupStatus.Unavailable, null);
        }
    }

    /// <summary>Login → Start → Ready を最大10秒待つ。タイムアウトしても例外にせず、Warning を出して続ける。</summary>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_client.LoginState == LoginState.LoggedIn)
        {
            return;
        }

        // 形式不正のトークンは、さっさと落とす
        TokenUtils.ValidateToken(TokenType.Bot, _settings.BotToken);

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Ready += () =>
        {
            ready.TrySetResult();
            return Task.CompletedTask;
        };
        await _client.LoginAsync(TokenType.Bot, _settings.BotToken);
        await _client.StartAsync();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Discord bot did not become ready within 10 seconds. Requests will get temporarily_unavailable until it does.");
        }
    }

    internal async Task StopAsync()
    {
        await _client.StopAsync();
        await _client.LogoutAsync();
    }
}
