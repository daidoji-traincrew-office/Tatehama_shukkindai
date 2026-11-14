using Tatehama.Shukkindai.Server.Repositories.Database;

namespace Tatehama.Shukkindai.Server.Services;

public interface IHealthService
{
    Task<bool> IsHealthy(CancellationToken cancellationToken);
}

/// <summary>
/// readiness。DB に接続できて、Discord と同期できているときだけ healthy。
/// 証明書の期限などは含めない(数ヶ月単位の話で、503 にするとかえって自分で障害を起こすため)。
/// </summary>
public sealed class HealthService(
    IDatabaseRepository databaseRepository,
    IDiscordService discordService,
    ILogger<HealthService> logger) : IHealthService
{
    public async Task<bool> IsHealthy(CancellationToken cancellationToken)
    {
        var databaseOk = false;
        try
        {
            databaseOk = await databaseRepository.CanConnect(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Health check: database connection failed.");
        }

        return databaseOk && discordService.IsReady();
    }
}
