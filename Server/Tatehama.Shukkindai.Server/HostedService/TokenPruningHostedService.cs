using Tatehama.Shukkindai.Server.Services;

namespace Tatehama.Shukkindai.Server.HostedService;

/// <summary>定期的に <see cref="ITokenPruningService"/> を呼ぶだけ。掃除の中身は Service にある。</summary>
public sealed class TokenPruningHostedService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ITokenPruningService>().Prune(stoppingToken);
        } while (await WaitForNextTick(timer, stoppingToken));
    }

    private static async Task<bool> WaitForNextTick(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
