namespace Tatehama.Shukkindai.Server.Services;

/// <summary>ログイン成功時のフック。将来 UC-02(社員番号の発番)を差し込む。refresh では呼ばない。</summary>
public interface IUserLoginObserver
{
    Task OnUserLoggedIn(ulong discordUserId, string username, CancellationToken cancellationToken);
}

public class NoOpUserLoginObserver : IUserLoginObserver
{
    public Task OnUserLoggedIn(ulong discordUserId, string username, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
