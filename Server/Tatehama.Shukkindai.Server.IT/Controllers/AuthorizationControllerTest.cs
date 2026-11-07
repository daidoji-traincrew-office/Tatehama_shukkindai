using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Tatehama.Shukkindai.Server.Authentication;
using Tatehama.Shukkindai.Server.IT.Collection;
using Tatehama.Shukkindai.Server.IT.Fixture;
using static Tatehama.Shukkindai.Server.IT.TestUtilities.IdpTestHelper;

namespace Tatehama.Shukkindai.Server.IT.Controllers;

[Collection(WebApplicationCollection.Name)]
public sealed class AuthorizationControllerTest(WebApplicationFixture fixture)
{
    private readonly WebApplicationFixture _factory = fixture;

    [Fact]
    public async Task AuthorizationCodeFlow_IssuesJwsAccessToken_WithOnlySubAndNameClaims()
    {
        var id = NewDiscordId();
        _factory.Lookup.SetMember(id, "alice");
        using var client = _factory.CreateIdpClient();

        var token = await LoginAsync(client, id);

        // JWS(ドットが2つ。暗号化されていない)
        Assert.Equal(2, token.AccessToken.Count(c => c == '.'));
        var header = DecodePart(token.AccessToken, 0);
        var payload = DecodePart(token.AccessToken, 1);
        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        Assert.False(header.TryGetProperty("enc", out _));

        Assert.Equal(WebApplicationFixture.Issuer, payload.GetProperty("iss").GetString());
        Assert.Equal(id.ToString(), payload.GetProperty("sub").GetString());
        Assert.Equal("alice", payload.GetProperty("name").GetString());
        Assert.False(payload.TryGetProperty("aud", out _));
        Assert.False(payload.TryGetProperty("role", out _));
        Assert.False(payload.TryGetProperty("oi_reg_id", out _));
        Assert.False(payload.TryGetProperty("oi_prvd_name", out _));
        var lifetime = payload.GetProperty("exp").GetInt64() - payload.GetProperty("iat").GetInt64();
        Assert.InRange(lifetime, 3590, 3610);
        Assert.InRange(token.ExpiresIn, 3590, 3600);
        Assert.False(string.IsNullOrEmpty(token.RefreshToken));

        AssertOnlyAllowedClaims(payload);
    }

    [Fact]
    public async Task Refresh_ReturnsNewTokens_AndReflectsLatestUsername()
    {
        var id = NewDiscordId();
        _factory.Lookup.SetMember(id, "dave");
        using var client = _factory.CreateIdpClient();
        var token = await LoginAsync(client, id);

        _factory.Lookup.SetMember(id, "dave-renamed");
        var refreshed = await ReadTokenAsync(await RefreshAsync(client, token.RefreshToken!));

        Assert.NotEqual(token.AccessToken, refreshed.AccessToken);
        Assert.False(string.IsNullOrEmpty(refreshed.RefreshToken));
        Assert.NotEqual(token.RefreshToken, refreshed.RefreshToken); // rolling
        var payload = DecodePart(refreshed.AccessToken, 1);
        Assert.Equal(id.ToString(), payload.GetProperty("sub").GetString());
        Assert.Equal("dave-renamed", payload.GetProperty("name").GetString());
        AssertOnlyAllowedClaims(payload);
        Assert.Equal(2, refreshed.AccessToken.Count(c => c == '.'));
    }

    [Fact]
    public async Task Revoke_ThenRefresh_IsInvalidGrant()
    {
        var id = NewDiscordId();
        _factory.Lookup.SetMember(id, "erin");
        using var client = _factory.CreateIdpClient();
        var token = await LoginAsync(client, id);

        var revoke = await client.PostAsync("connect/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token.RefreshToken!,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = ClientId,
        }), Ct);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var refresh = await RefreshAsync(client, token.RefreshToken!);
        Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
        var json = await refresh.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("invalid_grant", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Refresh_AfterLeavingTheGuild_IsInvalidGrant()
    {
        var id = NewDiscordId();
        _factory.Lookup.SetMember(id, "frank");
        using var client = _factory.CreateIdpClient();
        var token = await LoginAsync(client, id);

        _factory.Lookup.RemoveMember(id);
        var refresh = await RefreshAsync(client, token.RefreshToken!);

        Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
        var json = await refresh.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("invalid_grant", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Authorize_NonMember_RedirectsWithAccessDenied()
    {
        var id = NewDiscordId(); // メンバーに登録しない
        using var client = _factory.CreateIdpClient();

        var (location, _) = await AuthorizeAsync(client, id);

        Assert.StartsWith(RedirectUri, location.AbsoluteUri);
        Assert.Equal("access_denied", QueryValue(location, "error"));
        Assert.Null(System.Web.HttpUtility.ParseQueryString(location.Query)["code"]);
    }

    [Fact]
    public async Task Authorize_BotNotReady_RedirectsWithTemporarilyUnavailable()
    {
        var id = NewDiscordId();
        _factory.Lookup.SetMember(id, "grace");
        using var client = _factory.CreateIdpClient();

        _factory.Lookup.IsReady = false;
        try
        {
            var (location, _) = await AuthorizeAsync(client, id);
            Assert.Equal("temporarily_unavailable", QueryValue(location, "error"));
        }
        finally
        {
            _factory.Lookup.IsReady = true;
        }
    }

    [Fact]
    public async Task Refresh_BotNotReady_IsTemporarilyUnavailable()
    {
        var id = NewDiscordId();
        _factory.Lookup.SetMember(id, "heidi");
        using var client = _factory.CreateIdpClient();
        var token = await LoginAsync(client, id);

        _factory.Lookup.IsReady = false;
        try
        {
            var refresh = await RefreshAsync(client, token.RefreshToken!);
            var body = await refresh.Content.ReadAsStringAsync(Ct);
            // token エンドポイントは invalid_grant などと同じく 400 で返す
            Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("temporarily_unavailable", json.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            _factory.Lookup.IsReady = true;
        }

        // トークンは無効になっておらず、Ready に戻れば更新できる
        var again = await RefreshAsync(client, token.RefreshToken!);
        Assert.True(again.IsSuccessStatusCode, await again.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task OfflineAccessScope_IsGranted()
    {
        var id = NewDiscordId();
        _factory.Lookup.SetMember(id, "ivan");
        using var client = _factory.CreateIdpClient();

        var token = await LoginAsync(client, id);

        Assert.Equal("offline_access", token.Scope);
    }

    [Fact]
    public async Task PostAuthorize_KeepsParameters_AndLoginCookieIsHardened()
    {
        var id = NewDiscordId();
        _factory.Lookup.SetMember(id, "leo");
        using var client = _factory.CreateIdpClient();
        var (verifier, challenge) = NewPkce();

        using var post = new HttpRequestMessage(HttpMethod.Post, "connect/authorize")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["response_type"] = "code",
                ["redirect_uri"] = RedirectUri,
                ["scope"] = "offline_access",
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = "xyz",
            }),
        };
        post.Headers.Add(TestDiscordAuthenticationHandler.UserIdHeader, id.ToString());
        var first = await client.SendAsync(post, Ct);
        Assert.Equal(HttpStatusCode.Found, first.StatusCode);

        var setCookie = Assert.Single(first.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("__Host-Shukkindai.Login=", setCookie);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);

        // 戻り先にフォームのパラメーターが引き継がれ、そのまま code が発行される
        var second = await client.GetAsync(first.Headers.Location, Ct);
        Assert.Equal(HttpStatusCode.Found, second.StatusCode);
        var code = QueryValue(second.Headers.Location!, "code");
        var token = await ReadTokenAsync(await PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        }));
        Assert.Equal(id.ToString(), DecodePart(token.AccessToken, 1).GetProperty("sub").GetString());
    }

    private static async Task<HttpResponseMessage> StartDeniedLoginAsync(HttpClient client, string discordError)
    {
        var (_, challenge) = NewPkce();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "connect/authorize?client_id=" + ClientId +
            "&response_type=code&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
            "&scope=offline_access&code_challenge=" + challenge + "&code_challenge_method=S256&state=denied-state");
        request.Headers.Add(TestDiscordAuthenticationHandler.DenyHeader, discordError);
        return await client.SendAsync(request, Ct); // → 外部ログインの代わりに callback へ
    }

    [Theory]
    [InlineData("access_denied", "access_denied")]
    [InlineData("temporarily_unavailable", "temporarily_unavailable")]
    [InlineData("server_error", "server_error")]
    [InlineData("invalid_request", "server_error")]
    [InlineData("unknown_error", "server_error")]
    public async Task DiscordConsentDenied_ReturnsErrorToClientRedirectUri_WithOriginalState(
        string discordError, string expectedError)
    {
        using var client = _factory.CreateIdpClient();

        // 1. authorize → 外部ログイン(拒否)→ callback
        var toCallback = await StartDeniedLoginAsync(client, discordError);
        Assert.True(toCallback.StatusCode == HttpStatusCode.Found, "step1: " + await toCallback.Content.ReadAsStringAsync(Ct));
        Assert.StartsWith("/auth/callback", toCallback.Headers.Location!.OriginalString);

        // 2. callback は失敗の印を Cookie に入れ、元の authorize の URL へ 302 で戻す(クライアントへは直接戻さない)
        var callback = await client.GetAsync(toCallback.Headers.Location, Ct);
        Assert.True(callback.StatusCode == HttpStatusCode.Found, "step2: " + await callback.Content.ReadAsStringAsync(Ct));
        Assert.StartsWith("/connect/authorize?", callback.Headers.Location!.OriginalString);
        Assert.Contains("__Host-Shukkindai.Login=", string.Join(";", callback.Headers.GetValues("Set-Cookie")));

        // 3. authorize が Cookie の印を見て、クライアントの redirect_uri へエラーと元の state を返す
        var authorize = await client.GetAsync(callback.Headers.Location, Ct);
        Assert.Equal(HttpStatusCode.Found, authorize.StatusCode);
        var location = authorize.Headers.Location!;
        Assert.StartsWith(RedirectUri, location.AbsoluteUri);
        Assert.Equal(expectedError, QueryValue(location, "error"));
        Assert.Equal("denied-state", QueryValue(location, "state"));
        Assert.Null(System.Web.HttpUtility.ParseQueryString(location.Query)["code"]);
        if (expectedError == "access_denied")
        {
            Assert.Contains("拒否", QueryValue(location, "error_description"));
        }

        // 4. 失敗の印は SignOut されていて、同じクライアントの次のログインに残らない
        var id = NewDiscordId();
        _factory.Lookup.SetMember(id, "after-denied");
        var token = await LoginAsync(client, id);
        Assert.Equal(id.ToString(), DecodePart(token.AccessToken, 1).GetProperty("sub").GetString());
    }

    [Fact]
    public async Task DiscordLoginFailure_WithoutReturnUri_ShowsReadableMessage_AndNoCookie()
    {
        // state token が検証できなかった場合など、戻り先が分からないときは、サーバー側でエラーを表示する
        using var client = _factory.CreateIdpClient();

        var response = await client.GetAsync("auth/callback?error=access_denied", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType!.ToString(), ignoreCase: true);
        Assert.Contains("やり直してください", await response.Content.ReadAsStringAsync(Ct));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task DiscordLoginFailure_WithNonLocalReturnUri_IsNotFollowed()
    {
        // 戻り先が外部 URL なら、(state token 由来なので通常は起きないが)リダイレクトしない
        using var client = _factory.CreateIdpClient();

        var response = await client.GetAsync(
            "auth/callback?error=access_denied&redirect=" + Uri.EscapeDataString("https://evil.example/"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task RealCallbackScheme_ErrorFromDiscordWithInvalidState_ReachesControllerAsReadableMessage()
    {
        // テスト用 scheme に差し替えず、OpenIddict 本物の callback 処理を通す。
        // EnableErrorPassthrough が無いと、OpenIddict 自身の "error:invalid_token" が表示されて終わる
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.Configure<ExternalLoginOptions>(o => o.CallbackScheme = OpenIddict.Client.WebIntegration.OpenIddictClientWebIntegrationConstants.Providers.Discord)));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost/"),
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("auth/callback?error=access_denied&state=not-a-valid-state-token", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("やり直してください", body);
        Assert.DoesNotContain("invalid_token", body);
    }
}
