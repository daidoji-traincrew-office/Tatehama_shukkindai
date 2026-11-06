namespace Tatehama.Shukkindai.Server.Authentication;

/// <summary>ログイン成功時のフック。将来 UC-02(社員番号の発番)を差し込む。refresh では呼ばない。</summary>
public interface IUserLoginObserver
{
    Task OnUserLoggedInAsync(ulong discordUserId, string username, CancellationToken cancellationToken);
}

public sealed class NoOpUserLoginObserver : IUserLoginObserver
{
    public Task OnUserLoggedInAsync(ulong discordUserId, string username, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
