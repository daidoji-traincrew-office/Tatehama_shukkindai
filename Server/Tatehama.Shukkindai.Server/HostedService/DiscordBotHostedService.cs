using Tatehama.Shukkindai.Server.Repositories.Discord;

namespace Tatehama.Shukkindai.Server.HostedService;

/// <summary>
/// Bot の起動と停止。Initialize の例外(ログイン失敗・ギルドが見つからない・同期のタイムアウト)は、そのまま投げて
/// アプリの起動を止める。ASP.NET Core は hosted service の StartAsync が全部終わってから listen を始めるので、
/// リクエストを受け付ける時点では、基本的にキャッシュは信用できる状態になっている。
/// </summary>
public sealed class DiscordBotHostedService(DiscordRepository discordRepository) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => discordRepository.Initialize(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => discordRepository.Logout();
}
