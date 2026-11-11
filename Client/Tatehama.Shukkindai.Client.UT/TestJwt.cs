using System.Text;
using System.Text.Json;

namespace Tatehama.Shukkindai.Client.UT;

/// <summary>署名なしのダミー JWT(payload の取り出しのテスト用)。</summary>
public static class TestJwt
{
    public static string Create(string sub, string? name, DateTimeOffset exp)
    {
        var payload = new Dictionary<string, object> { ["sub"] = sub, ["exp"] = exp.ToUnixTimeSeconds() };
        if (name is not null)
        {
            payload["name"] = name;
        }

        return $"{Encode("{\"alg\":\"RS256\"}")}.{Encode(JsonSerializer.Serialize(payload))}.c2ln";
    }

    public static string Encode(string json)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
