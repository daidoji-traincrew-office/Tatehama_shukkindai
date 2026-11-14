namespace Tatehama.Shukkindai.Client.Core;

/// <summary>状態を UI の表示文言にする(純粋関数)。</summary>
public static class StatusText
{
    public static string Format(SessionStatus status, TimeZoneInfo timeZone)
    {
        switch (status.State)
        {
            case SessionState.SigningIn:
                return "更新中…";
            case SessionState.SignedIn:
                return $"ログイン中: {FormatUser(status.User)}\n有効期限 {FormatExpiry(status.User, timeZone)}";
            case SessionState.Error:
                return $"エラー: {FormatFailure(status.Failure)}";
            default:
                return status.Failure is { } failure
                    ? $"未ログイン\n{FormatFailure(failure)}"
                    : "未ログイン";
        }
    }

    public static string FormatFailure(AuthFailureKind? kind)
    {
        return kind switch
        {
            AuthFailureKind.AccessDenied => "Discord サーバーのメンバーではないか、ログインが拒否されました",
            AuthFailureKind.TemporarilyUnavailable => "出勤台が一時的に使えません。しばらくしてから再試行してください",
            AuthFailureKind.InvalidGrant => "ログインの有効期限が切れました。再度ログインしてください",
            _ => "出勤台に接続できません。再試行しています",
        };
    }

    private static string FormatUser(AccessTokenClaims? user)
    {
        return user is null ? "(不明)" : $"{user.Name ?? "(名前なし)"}({user.Subject})";
    }

    private static string FormatExpiry(AccessTokenClaims? user, TimeZoneInfo timeZone)
    {
        return user is null ? "-" : TimeZoneInfo.ConvertTime(user.ExpiresAt, timeZone).ToString("HH:mm");
    }
}
