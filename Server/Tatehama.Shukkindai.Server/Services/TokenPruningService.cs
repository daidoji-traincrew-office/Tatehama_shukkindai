using OpenIddict.Abstractions;

namespace Tatehama.Shukkindai.Server.Services;

public interface ITokenPruningService
{
    /// <summary>1回分の掃除。例外はログを出して次回に回す。</summary>
    Task Prune(CancellationToken cancellationToken);
}

/// <summary>
/// 期限切れのトークンと、使われなくなった authorization を削除する。
/// 閾値は refresh token の寿命(7日)より長くする。
/// </summary>
public class TokenPruningService(
    IOpenIddictTokenManager tokenManager,
    IOpenIddictAuthorizationManager authorizationManager,
    TimeProvider timeProvider,
    ILogger<TokenPruningService> logger) : ITokenPruningService
{
    public static readonly TimeSpan Threshold = TimeSpan.FromDays(14);

    public async Task Prune(CancellationToken cancellationToken)
    {
        var threshold = timeProvider.GetUtcNow() - Threshold;
        // トークンを先に消すと、authorization の掃除対象(トークンを持たないもの)が増える
        try
        {
            await tokenManager.PruneAsync(threshold, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Failed to prune OpenIddict tokens.");
        }

        try
        {
            await authorizationManager.PruneAsync(threshold, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Failed to prune OpenIddict authorizations.");
        }
    }
}
