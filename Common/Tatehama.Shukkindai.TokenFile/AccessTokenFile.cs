using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tatehama.Shukkindai.TokenFile;

/// <summary>
/// 他のアプリに渡すアクセストークンファイルの中身(暗号化前)。refresh token は入れない。
/// </summary>
public sealed record AccessTokenFile(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_at")] long ExpiresAt)
{
    public const int CurrentVersion = 1;

    public static AccessTokenFile Create(string accessToken, DateTimeOffset expiresAt)
    {
        return new(CurrentVersion, accessToken, expiresAt.ToUnixTimeSeconds());
    }

    public byte[] ToJsonBytes()
    {
        return JsonSerializer.SerializeToUtf8Bytes(this);
    }

    public static AccessTokenFile? FromJsonBytes(byte[] bytes)
    {
        return JsonSerializer.Deserialize<AccessTokenFile>(bytes);
    }
}
