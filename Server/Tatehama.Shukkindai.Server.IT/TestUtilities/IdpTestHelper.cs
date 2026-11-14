using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using Tatehama.Shukkindai.Server.IT.Fixture;

namespace Tatehama.Shukkindai.Server.IT.TestUtilities;

/// <summary>authorize・token の往復など、IT で共通に使う補助。</summary>
public static class IdpTestHelper
{
    public const string ClientId = "Tatehama_Shukkindai_Client";
    public const string RedirectUri = "http://localhost:51180/";

    /// <summary>Cookie を保持し、リダイレクトを自動では辿らない HttpClient(ログイン途中の Cookie を確かめるため)。</summary>
    public static HttpClient CreateIdpClient<TEntryPoint>(this WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class =>
        factory.CreateClient(new()
        {
            BaseAddress = new("https://localhost/"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

    private static long _idCounter = 900_000_000_000_000_000L + Random.Shared.NextInt64(1_000_000);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static ulong NewDiscordId() => (ulong)Interlocked.Increment(ref _idCounter);

    public sealed record TokenResponse(string AccessToken, string? RefreshToken, int ExpiresIn, string? Scope);

    public static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    /// <summary>authorize を最後まで進め、クライアントのリダイレクト先(302 の Location)を返す。</summary>
    public static async Task<(Uri Location, string Verifier)> Authorize(HttpClient client, ulong discordId,
        string scope = "offline_access",
        string challengeMethod = "S256")
    {
        var (verifier, challenge) = NewPkce();
        var url = "connect/authorize?client_id=" + ClientId +
                  "&response_type=code&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                  "&scope=" + Uri.EscapeDataString(scope) +
                  "&code_challenge=" + challenge + "&code_challenge_method=" + challengeMethod + "&state=xyz";

        // 1. 外部ログインへ Challenge(テスト scheme が Cookie を発行して authorize に戻す)
        using var first = new HttpRequestMessage(HttpMethod.Get, url);
        first.Headers.Add(TestDiscordAuthenticationHandler.UserIdHeader, discordId.ToString());
        var firstResponse = await client.SendAsync(first, Ct);
        Assert.Equal(HttpStatusCode.Found, firstResponse.StatusCode);

        // 2. authorize に戻る → クライアントへ 302
        var secondResponse = await client.GetAsync(firstResponse.Headers.Location, Ct);
        Assert.Equal(HttpStatusCode.Found, secondResponse.StatusCode);
        return (secondResponse.Headers.Location!, verifier);
    }

    public static async Task<HttpResponseMessage> SendAuthorize(HttpClient client, ulong discordId,
        string scope, string challengeMethod)
    {
        var (_, challenge) = NewPkce();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "connect/authorize?client_id=" + ClientId +
            "&response_type=code&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
            "&scope=" + Uri.EscapeDataString(scope) +
            "&code_challenge=" + challenge + "&code_challenge_method=" + challengeMethod + "&state=xyz");
        request.Headers.Add(TestDiscordAuthenticationHandler.UserIdHeader, discordId.ToString());
        return await client.SendAsync(request, Ct);
    }

    public static async Task<HttpResponseMessage> PostToken(HttpClient client, Dictionary<string, string> form) =>
        await client.PostAsync("connect/token", new FormUrlEncodedContent(form), Ct);

    public static async Task<TokenResponse> ReadToken(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        return new(
            root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
            root.GetProperty("expires_in").GetInt32(),
            root.TryGetProperty("scope", out var s) ? s.GetString() : null);
    }

    public static async Task<TokenResponse> Login(HttpClient client, ulong discordId)
    {
        var (location, verifier) = await Authorize(client, discordId);
        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        Assert.Equal("http://localhost:51180/", location.GetLeftPart(UriPartial.Path));
        Assert.Equal("xyz", query["state"]);
        var code = query["code"];
        Assert.False(string.IsNullOrEmpty(code), "authorization code が無い: " + location);

        var response = await PostToken(client, new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        });
        return await ReadToken(response);
    }

    public static Task<HttpResponseMessage> Refresh(HttpClient client, string refreshToken) =>
        PostToken(client, new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
        });

    public static JsonElement DecodePart(string jwt, int index)
    {
        var parts = jwt.Split('.');
        using var doc = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[index]));
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// ユーザー由来の claim は sub と name だけ。残りは OpenIddict のプロトコル claim(実際の出力から作った許可リスト)。
    /// </summary>
    public static void AssertOnlyAllowedClaims(JsonElement payload)
    {
        var allowed = new HashSet<string>
        {
            "sub", "name",
            "iss", "iat", "exp", "jti", "scope", "client_id", "oi_prst", "oi_au_id", "oi_tkn_id",
        };
        var unexpected = payload.EnumerateObject().Select(p => p.Name).Where(n => !allowed.Contains(n)).ToList();
        Assert.True(unexpected.Count == 0, "想定外の claim: " + string.Join(",", unexpected) + " / " + payload);
        Assert.False(payload.TryGetProperty("aud", out _));
    }

    public static string QueryValue(Uri uri, string key) => System.Web.HttpUtility.ParseQueryString(uri.Query)[key]!;
}
