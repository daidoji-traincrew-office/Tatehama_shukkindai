using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tatehama.Shukkindai.TokenFile;

namespace Tatehama.Shukkindai.Client.Core;

public enum SessionState
{
    SignedOut,
    SigningIn,
    SignedIn,
    Error,
}

/// <summary>UI が表示に使う現在の状態。</summary>
public sealed record SessionStatus(
    SessionState State,
    AccessTokenClaims? User = null,
    AuthFailureKind? Failure = null);

/// <summary>
/// ログイン状態と、アクセストークンの定期更新を持つ。更新と保存は <see cref="_gate"/> で 1 か所に直列化する
/// (refresh token は rolling なので、同時に使うと古い方が失効しうる)。
/// </summary>
public sealed class SessionManager(
    IShukkindaiAuthClient authClient,
    RefreshTokenStore refreshTokenStore,
    AccessTokenFileWriter accessTokenFileWriter,
    IOptions<ShukkindaiClientOptions> options,
    TimeProvider timeProvider,
    ILogger<SessionManager> logger) : IDisposable
{
    private static readonly TimeSpan BackoffBase = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BackoffMax = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinimumRefreshDelay = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITimer? _timer;
    private string? _refreshToken;
    private int _failureCount;

    public SessionStatus Status { get; private set; } = new(SessionState.SignedOut);

    public event Action<SessionStatus>? StatusChanged;

    /// <summary>起動時。保存済みの refresh token があれば更新して復元する。</summary>
    public async Task Start(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var stored = refreshTokenStore.Load();
            if (stored is null)
            {
                return;
            }

            _refreshToken = stored;
            SetStatus(new SessionStatus(SessionState.SigningIn));
            await RefreshLocked(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignIn(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previous = Status;
            SetStatus(new SessionStatus(SessionState.SigningIn));
            AuthResult result;
            try
            {
                result = await authClient.Login(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SetStatus(previous);
                return;
            }

            Apply(result);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>ログアウト。revoke の失敗は無視して、両ファイルを削除する。</summary>
    public async Task SignOut(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var token = _refreshToken ?? refreshTokenStore.Load();
            if (token is not null)
            {
                try
                {
                    await authClient.Revoke(token, cancellationToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "refresh token の revoke に失敗した(続行する)。");
                }
            }

            ClearLocked();
            SetStatus(new SessionStatus(SessionState.SignedOut));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _gate.Dispose();
    }

    private async Task RefreshLocked(CancellationToken cancellationToken)
    {
        if (_refreshToken is null)
        {
            return;
        }

        AuthResult result;
        try
        {
            result = await authClient.Refresh(_refreshToken, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Apply(result);
    }

    private void Apply(AuthResult result)
    {
        switch (result)
        {
            case AuthResult.Succeeded success:
                ApplySuccess(success);
                break;
            case AuthResult.Failed { Kind: AuthFailureKind.InvalidGrant or AuthFailureKind.AccessDenied } failed:
                ClearLocked();
                SetStatus(new SessionStatus(SessionState.SignedOut, Failure: failed.Kind));
                break;
            case AuthResult.Failed failed:
                // 一時的な失敗。トークンファイルは消さず、指数バックオフで再試行する
                logger.LogWarning("トークンの取得に失敗した({Kind})。再試行する。", failed.Kind);
                SetStatus(new SessionStatus(SessionState.Error, Status.User, failed.Kind));
                if (_refreshToken is not null)
                {
                    ScheduleRetry();
                }

                break;
        }
    }

    private void ApplySuccess(AuthResult.Succeeded success)
    {
        _refreshToken = success.RefreshToken;
        try
        {
            // refresh は rolling。先に必ず保存し直し、そのあとアクセストークンを書き出す
            refreshTokenStore.Save(success.RefreshToken);
            accessTokenFileWriter.Write(success.AccessToken, success.ExpiresAt);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            logger.LogError(e, "トークンファイルの保存に失敗した。再試行する。");
            SetStatus(new SessionStatus(SessionState.Error, Status.User, AuthFailureKind.Transient));
            ScheduleRetry();
            return;
        }

        _failureCount = 0;
        SetStatus(new SessionStatus(SessionState.SignedIn, AccessTokenClaims.Parse(success.AccessToken)));
        var delay = success.ExpiresAt - options.Value.RefreshBeforeExpiry - timeProvider.GetUtcNow();
        Schedule(delay < MinimumRefreshDelay ? MinimumRefreshDelay : delay);
    }

    private void ScheduleRetry()
    {
        var factor = Math.Pow(2, Math.Min(_failureCount, 10));
        _failureCount++;
        var delay = TimeSpan.FromTicks((long)Math.Min(BackoffBase.Ticks * factor, BackoffMax.Ticks));
        Schedule(delay);
    }

    private void Schedule(TimeSpan delay)
    {
        _timer?.Dispose();
        _timer = timeProvider.CreateTimer(_ => _ = RunScheduledRefresh(), null, delay, Timeout.InfiniteTimeSpan);
    }

    private async Task RunScheduledRefresh()
    {
        try
        {
            await _gate.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await RefreshLocked(CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError(e, "トークンの定期更新で想定外のエラーが起きた。");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ClearLocked()
    {
        _timer?.Dispose();
        _timer = null;
        _refreshToken = null;
        _failureCount = 0;
        refreshTokenStore.Delete();
        accessTokenFileWriter.Delete();
    }

    private void SetStatus(SessionStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }
}
