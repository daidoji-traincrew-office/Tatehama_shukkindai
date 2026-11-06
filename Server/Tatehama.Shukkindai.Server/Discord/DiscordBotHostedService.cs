namespace Tatehama.Shukkindai.Server.Discord;

public sealed class DiscordBotHostedService(DiscordMemberLookup lookup) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => lookup.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => lookup.StopAsync();
}
