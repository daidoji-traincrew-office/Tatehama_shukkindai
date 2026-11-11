using Tatehama.Shukkindai.Client.Core;

namespace Tatehama.Shukkindai.Client.UT;

public class StatusTextTest
{
    private static readonly TimeZoneInfo Jst = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");

    [Fact]
    public void Format_SignedOut_ReturnsNotSignedIn()
    {
        var text = StatusText.Format(new(SessionState.SignedOut), Jst);

        Assert.Equal("未ログイン", text);
    }

    [Fact]
    public void Format_SignedIn_ShowsUserAndLocalExpiry()
    {
        var user = new AccessTokenClaims("123", "taro", new(2026, 1, 1, 3, 30, 0, TimeSpan.Zero));

        var text = StatusText.Format(new(SessionState.SignedIn, user), Jst);

        Assert.Contains("taro(123)", text);
        Assert.Contains("有効期限 12:30", text);
    }

    [Fact]
    public void Format_SigningIn_ShowsUpdating()
    {
        var text = StatusText.Format(new(SessionState.SigningIn), Jst);

        Assert.Equal("更新中…", text);
    }

    [Theory]
    [InlineData(AuthFailureKind.AccessDenied, "Discord サーバーのメンバーではないか")]
    [InlineData(AuthFailureKind.TemporarilyUnavailable, "一時的に使えません")]
    [InlineData(AuthFailureKind.Transient, "再試行しています")]
    public void Format_Error_ShowsMessageForKind(AuthFailureKind kind, string expected)
    {
        var text = StatusText.Format(new(SessionState.Error, Failure: kind), Jst);

        Assert.Contains(expected, text);
    }

    [Fact]
    public void Format_SignedOutAfterAccessDenied_ShowsReason()
    {
        var text = StatusText.Format(new(SessionState.SignedOut, Failure: AuthFailureKind.AccessDenied), Jst);

        Assert.StartsWith("未ログイン", text);
        Assert.Contains("拒否", text);
    }
}
