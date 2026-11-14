using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Tatehama.Shukkindai.Client.Core;
using Tatehama.Shukkindai.TokenFile;

namespace Tatehama.Shukkindai.Client.UT;

public sealed class SessionManagerTest : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly FakeTokenProtector _protector = new();
    private readonly FakeTimeProvider _time = new(new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero));
    private readonly Mock<IShukkindaiAuthClient> _auth = new();
    private readonly RefreshTokenStore _refreshStore;
    private readonly AccessTokenFileWriter _accessWriter;
    private readonly AccessTokenFileReader _accessReader;
    private readonly SessionManager _sut;
    private readonly List<SessionStatus> _statuses = [];

    public SessionManagerTest()
    {
        var accessPath = Path.Combine(_directory, "access-token.bin");
        _refreshStore = new(_protector, Path.Combine(_directory, "refresh-token.bin"));
        _accessWriter = new(_protector, accessPath);
        _accessReader = new(_protector, accessPath, _time);
        _sut = new(
            _auth.Object,
            _refreshStore,
            _accessWriter,
            Options.Create(new ShukkindaiClientOptions()),
            _time,
            NullLogger<SessionManager>.Instance);
        _sut.StatusChanged += _statuses.Add;
    }

    public void Dispose()
    {
        _sut.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private AuthResult.Succeeded Success(string refreshToken, TimeSpan? lifetime = null)
    {
        var expiresAt = _time.GetUtcNow().Add(lifetime ?? TimeSpan.FromHours(1));
        return new(TestJwt.Create("900000000000740692", "alice", expiresAt), refreshToken, expiresAt);
    }

    private static AuthResult.Failed Failure(AuthFailureKind kind)
    {
        return new(kind);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "条件が時間内に満たされなかった。");
    }

    [Fact]
    public async Task Start_NoStoredRefreshToken_StaysSignedOutWithoutCallingServer()
    {
        await _sut.Start(CancellationToken.None);

        Assert.Equal(SessionState.SignedOut, _sut.Status.State);
        _auth.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Start_StoredRefreshToken_RefreshesAndSignsIn()
    {
        _refreshStore.Save("refresh-1");
        _auth.Setup(a => a.Refresh("refresh-1", It.IsAny<CancellationToken>())).ReturnsAsync(Success("refresh-2"));

        await _sut.Start(CancellationToken.None);

        Assert.Equal(SessionState.SignedIn, _sut.Status.State);
        Assert.Equal("alice", _sut.Status.User?.Name);
        Assert.Equal("refresh-2", _refreshStore.Load());
        Assert.IsType<AccessTokenReadResult.Valid>(_accessReader.Read());
    }

    [Fact]
    public async Task Start_AccessTokenFileCannotBeWritten_RefreshTokenIsAlreadySaved()
    {
        // アクセストークンの書き出し先をディレクトリにして、書き出しだけを失敗させる
        var blockedPath = Path.Combine(_directory, "blocked");
        Directory.CreateDirectory(blockedPath);
        using var sut = new SessionManager(
            _auth.Object,
            _refreshStore,
            new(_protector, blockedPath),
            Options.Create(new ShukkindaiClientOptions()),
            _time,
            NullLogger<SessionManager>.Instance);
        _refreshStore.Save("refresh-1");
        _auth.Setup(a => a.Refresh("refresh-1", It.IsAny<CancellationToken>())).ReturnsAsync(Success("refresh-2"));

        await sut.Start(CancellationToken.None);

        // refresh は rolling なので、先に保存し直している(書き出しが失敗しても新しい refresh token は残る)
        Assert.Equal("refresh-2", _refreshStore.Load());
        Assert.Equal(SessionState.Error, sut.Status.State);
    }

    [Fact]
    public async Task Start_InvalidGrant_SignsOutAndDeletesBothFiles()
    {
        _refreshStore.Save("refresh-1");
        _accessWriter.Write("old-access", _time.GetUtcNow().AddMinutes(30));
        _auth.Setup(a => a.Refresh(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Failure(AuthFailureKind.InvalidGrant));

        await _sut.Start(CancellationToken.None);

        Assert.Equal(SessionState.SignedOut, _sut.Status.State);
        Assert.Null(_refreshStore.Load());
        Assert.IsType<AccessTokenReadResult.Missing>(_accessReader.Read());
    }

    [Fact]
    public async Task Start_AccessDenied_SignsOutAndDeletesBothFiles()
    {
        _refreshStore.Save("refresh-1");
        _accessWriter.Write("old-access", _time.GetUtcNow().AddMinutes(30));
        _auth.Setup(a => a.Refresh(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Failure(AuthFailureKind.AccessDenied));

        await _sut.Start(CancellationToken.None);

        Assert.Equal(SessionState.SignedOut, _sut.Status.State);
        Assert.Equal(AuthFailureKind.AccessDenied, _sut.Status.Failure);
        Assert.Null(_refreshStore.Load());
        Assert.IsType<AccessTokenReadResult.Missing>(_accessReader.Read());
    }

    [Theory]
    [InlineData(AuthFailureKind.TemporarilyUnavailable)]
    [InlineData(AuthFailureKind.Transient)]
    public async Task Start_TemporaryFailure_KeepsFilesAndRetriesWithBackoff(AuthFailureKind kind)
    {
        _refreshStore.Save("refresh-1");
        _accessWriter.Write("old-access", _time.GetUtcNow().AddMinutes(30));
        var calls = 0;
        _auth.Setup(a => a.Refresh("refresh-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++calls < 3 ? Failure(kind) : Success("refresh-2"));

        await _sut.Start(CancellationToken.None);

        Assert.Equal(SessionState.Error, _sut.Status.State);
        Assert.Equal(kind, _sut.Status.Failure);
        Assert.Equal("refresh-1", _refreshStore.Load());
        Assert.IsType<AccessTokenReadResult.Valid>(_accessReader.Read());

        // 1 回目の再試行は 30 秒後(失敗のまま)、2 回目はその倍の 60 秒後(成功)
        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(1, calls);
        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => calls == 2 && _sut.Status.State == SessionState.Error);
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(2, calls);
        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => _sut.Status.State == SessionState.SignedIn);
        Assert.Equal("refresh-2", _refreshStore.Load());
    }

    [Fact]
    public async Task Start_RepeatedTemporaryFailure_BackoffIsCapped()
    {
        _refreshStore.Save("refresh-1");
        var calls = 0;
        _auth.Setup(a => a.Refresh(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls++;
                return Failure(AuthFailureKind.Transient);
            });
        await _sut.Start(CancellationToken.None);

        // 30s, 60s, 120s, ... は 15 分で頭打ち。長く進めても、15 分ごとの再試行になる
        var expected = 1;
        for (var i = 0; i < 12; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(15));
            expected++;
            var target = expected;
            await WaitUntil(() => calls >= target);
        }

        Assert.Equal(13, calls);
    }

    [Fact]
    public async Task Start_SuccessfulRefresh_NextRefreshIsTenMinutesBeforeExpiry()
    {
        _refreshStore.Save("refresh-1");
        var calls = 0;
        _auth.Setup(a => a.Refresh(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Success($"refresh-{++calls + 1}"));

        await _sut.Start(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(49));
        Assert.Equal(1, calls);
        _time.Advance(TimeSpan.FromMinutes(1));

        await WaitUntil(() => calls == 2);
        await WaitUntil(() => _refreshStore.Load() == "refresh-3");
    }

    [Fact]
    public async Task Refresh_UsesTheRefreshTokenReturnedByThePreviousRefresh()
    {
        _refreshStore.Save("refresh-1");
        var used = new List<string>();
        _auth.Setup(a => a.Refresh(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string token, CancellationToken _) =>
            {
                used.Add(token);
                return Success($"refresh-{used.Count + 1}");
            });

        await _sut.Start(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(50));
        await WaitUntil(() => used.Count == 2);

        Assert.Equal(["refresh-1", "refresh-2"], used);
    }

    [Fact]
    public async Task SignIn_Success_SignsInAndWritesBothFiles()
    {
        _auth.Setup(a => a.Login(It.IsAny<CancellationToken>())).ReturnsAsync(Success("refresh-1"));

        await _sut.SignIn(CancellationToken.None);

        Assert.Equal(SessionState.SignedIn, _sut.Status.State);
        Assert.Equal([SessionState.SigningIn, SessionState.SignedIn], _statuses.Select(s => s.State));
        Assert.Equal("refresh-1", _refreshStore.Load());
        Assert.IsType<AccessTokenReadResult.Valid>(_accessReader.Read());
    }

    [Fact]
    public async Task SignIn_AccessDenied_StaysSignedOutWithFailure()
    {
        _auth.Setup(a => a.Login(It.IsAny<CancellationToken>())).ReturnsAsync(Failure(AuthFailureKind.AccessDenied));

        await _sut.SignIn(CancellationToken.None);

        Assert.Equal(new(SessionState.SignedOut, Failure: AuthFailureKind.AccessDenied), _sut.Status);
        Assert.Null(_refreshStore.Load());
    }

    [Fact]
    public async Task SignIn_Cancelled_ReturnsToSignedOut()
    {
        _auth.Setup(a => a.Login(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        await _sut.SignIn(CancellationToken.None);

        Assert.Equal(SessionState.SignedOut, _sut.Status.State);
    }

    [Fact]
    public async Task SignOut_SignedIn_RevokesAndDeletesBothFiles()
    {
        _auth.Setup(a => a.Login(It.IsAny<CancellationToken>())).ReturnsAsync(Success("refresh-1"));
        _auth.Setup(a => a.Revoke("refresh-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await _sut.SignIn(CancellationToken.None);

        await _sut.SignOut(CancellationToken.None);

        _auth.Verify(a => a.Revoke("refresh-1", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(SessionState.SignedOut, _sut.Status.State);
        Assert.Null(_refreshStore.Load());
        Assert.IsType<AccessTokenReadResult.Missing>(_accessReader.Read());
    }

    [Fact]
    public async Task SignOut_RevokeThrows_StillDeletesBothFiles()
    {
        _auth.Setup(a => a.Login(It.IsAny<CancellationToken>())).ReturnsAsync(Success("refresh-1"));
        _auth.Setup(a => a.Revoke(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException());
        await _sut.SignIn(CancellationToken.None);

        await _sut.SignOut(CancellationToken.None);

        Assert.Equal(SessionState.SignedOut, _sut.Status.State);
        Assert.Null(_refreshStore.Load());
        Assert.IsType<AccessTokenReadResult.Missing>(_accessReader.Read());
    }

    [Fact]
    public async Task SignOut_AfterSignOut_NoMoreScheduledRefresh()
    {
        _auth.Setup(a => a.Login(It.IsAny<CancellationToken>())).ReturnsAsync(Success("refresh-1"));
        _auth.Setup(a => a.Revoke(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await _sut.SignIn(CancellationToken.None);
        await _sut.SignOut(CancellationToken.None);

        _time.Advance(TimeSpan.FromHours(2));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        _auth.Verify(a => a.Refresh(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
