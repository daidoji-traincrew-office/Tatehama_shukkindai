namespace Tatehama.Shukkindai.Server.Repositories.Database;

public interface IDatabaseRepository
{
    /// <summary>DB に接続できるか。接続に失敗したときの例外はそのまま投げる(キャンセルを含む)。</summary>
    Task<bool> CanConnect(CancellationToken cancellationToken);
}
