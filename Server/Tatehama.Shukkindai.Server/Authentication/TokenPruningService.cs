using OpenIddict.Abstractions;

namespace Tatehama.Shukkindai.Server.Authentication;

/// <summary>
/// 期限切れのトークンと、使われなくなった authorization を定期的に削除する。
/// 閾値は refresh token の寿命(7日)より長くする。
/// </summary>
public sealed class TokenPruningService(IServiceScopeFactory scopeFactory, ILogger<TokenPruningService> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    public static readonly TimeSpan Threshold = TimeSpan.FromDays(14);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await PruneOnceAsync(stoppingToken);
        } while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
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

    /// <summary>1回分の掃除。例外はログを出して次回に回す。</summary>
    public async Task PruneOnceAsync(CancellationToken cancellationToken)
    {
        var threshold = DateTimeOffset.UtcNow - Threshold;
        await using var scope = scopeFactory.CreateAsyncScope();
        // トークンを先に消すと、authorization の掃除対象(トークンを持たないもの)が増える
        try
        {
            await scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>()
                .PruneAsync(threshold, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Failed to prune OpenIddict tokens.");
        }

        try
        {
            await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>()
                .PruneAsync(threshold, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Failed to prune OpenIddict authorizations.");
        }
    }
}
