using System.Text.Json;

namespace Tatehama.Shukkindai.Client.Core;

/// <summary>アクセストークン(JWT)の payload から表示用の claim を取り出す。署名は検証しない。</summary>
public sealed record AccessTokenClaims(string Subject, string? Name, DateTimeOffset ExpiresAt)
{
    public static AccessTokenClaims? Parse(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(DecodeBase64Url(parts[1]));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("sub", out var sub) || sub.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var expSeconds))
            {
                return null;
            }

            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;
            return new(sub.GetString()!, name, DateTimeOffset.FromUnixTimeSeconds(expSeconds));
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Convert.FromBase64String(base64);
    }
}
