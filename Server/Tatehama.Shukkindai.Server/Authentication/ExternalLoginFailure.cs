using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tatehama.Shukkindai.Server.Authentication;

/// <summary>
/// Discord での認可が失敗したことを、callback から authorize へ伝える Cookie の印。
/// Discord が返した error を、クライアントへ返す error に写像する。
/// </summary>
public static class ExternalLoginFailure
{
    public const string ClaimType = "external_login_error";

    /// <summary>
    /// access_denied(利用者の拒否・キャンセル)はそのまま、temporarily_unavailable もそのまま。
    /// それ以外(server_error、invalid_request、不明なものなど)は server_error にする。
    /// </summary>
    public static string MapError(string? discordError) => discordError switch
    {
        Errors.AccessDenied => Errors.AccessDenied,
        Errors.TemporarilyUnavailable => Errors.TemporarilyUnavailable,
        _ => Errors.ServerError,
    };

    public static string Description(string error) => error switch
    {
        Errors.AccessDenied => "Discord での認可が拒否されました。",
        Errors.TemporarilyUnavailable => "Discord が一時的に利用できません。しばらくしてから再試行してください。",
        _ => "Discord でのログインに失敗しました。",
    };
}
