using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using Tatehama.Shukkindai.Server.Authentication;
using Microsoft.IdentityModel.Tokens;

namespace Tatehama.Shukkindai.Server.IT;

public sealed class IdpFlowTests : IClassFixture<IdpFactory>
{
    private const string ClientId = "Tatehama_Shukkindai_Client";
    private const string RedirectUri = "http://localhost:51180/";
    private static long _idCounter = 900_000_000_000_000_000L + Random.Shared.NextInt64(1_000_000);

    private readonly IdpFactory _factory;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public IdpFlowTests(IdpFactory factory) => _factory = factory;

    private static ulong NewDiscordId() => (ulong)Interlocked.Increment(ref _idCounter);

    private sealed record TokenResponse(string AccessToken, string? RefreshToken, int ExpiresIn, string? Scope);

    private static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    /// <summary>authorize を最後まで進め、クライアントのリダイレクト先(302 の Location)を返す。</summary>
    private static async Task<(Uri Location, string Verifier)> AuthorizeAsync(HttpClient client, ulong discordId,
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

    private static async Task<HttpResponseMessage> SendAuthorizeAsync(HttpClient client, ulong discordId,
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

    private static async Task<HttpResponseMessage> PostTokenAsync(HttpClient client, Dictionary<string, string> form) =>
        await client.PostAsync("connect/token", new FormUrlEncodedContent(form), Ct);

    private static async Task<TokenResponse> ReadTokenAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        return new TokenResponse(
            root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
            root.GetProperty("expires_in").GetInt32(),
            root.TryGetProperty("scope", out var s) ? s.GetString() : null);
    }

    private static async Task<TokenResponse> LoginAsync(HttpClient client, ulong discordId)
    {
        var (location, verifier) = await AuthorizeAsync(client, discordId);
        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        Assert.Equal("http://localhost:51180/", location.GetLeftPart(UriPartial.Path));
        Assert.Equal("xyz", query["state"]);
        var code = query["code"];
        Assert.False(string.IsNullOrEmpty(code), "authorization code が無い: " + location);

        var response = await PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code!,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        });
        return await ReadTokenAsync(response);
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
        });

    private static JsonElement DecodePart(string jwt, int index)
    {
        var parts = jwt.Split('.');
        using var doc = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[index]));
        return doc.RootElement.Clone();
    }

    private X509Certificate2 LoadCer() => X509CertificateLoader.LoadCertificateFromFile(
        Path.Combine(_factory.CertificateDirectory, "server-signing-certificate.cer"));

    /// <summary>
    /// ユーザー由来の claim は sub と name だけ。残りは OpenIddict のプロトコル claim(実際の出力から作った許可リスト)。
    /// </summary>
    private static void AssertOnlyAllowedClaims(JsonElement payload)
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

    private static string QueryValue(Uri uri, string key) => System.Web.HttpUtility.ParseQueryString(uri.Query)[key]!;

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

        Assert.Equal(IdpFactory.Issuer, payload.GetProperty("iss").GetString());
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
    public async Task AccessToken_ValidatesStaticallyWithCerFile()
    {
        // Crew と TTC が行う静的検証そのもの
        var id = NewDiscordId();
        _factory.Lookup.SetMember(id, "bob");
        using var client = _factory.CreateIdpClient();
        var token = await LoginAsync(client, id);

        using var cer = LoadCer();
        Assert.False(cer.HasPrivateKey);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.AccessToken, new TokenValidationParameters
        {
            ValidIssuer = IdpFactory.Issuer,
            IssuerSigningKey = new X509SecurityKey(cer),
            ValidateIssuer = true,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
        });

        Assert.True(result.IsValid, result.Exception?.ToString());
        Assert.Equal(id.ToString(), result.Claims["sub"]);

        // 別の鍵では通らない
        using var other = RSA.Create(2048);
        var wrong = await new JsonWebTokenHandler().ValidateTokenAsync(token.AccessToken, new TokenValidationParameters
        {
            ValidIssuer = IdpFactory.Issuer,
            IssuerSigningKey = new RsaSecurityKey(other),
            ValidateAudience = false,
        });
        Assert.False(wrong.IsValid);
    }

    [Fact]
    public async Task Discovery_MatchesIssuer_AndJwksMatchesCer()
    {
        var id = NewDiscordId();
        _factory.Lookup.SetMember(id, "carol");
        using var client = _factory.CreateIdpClient();
        var token = await LoginAsync(client, id);

        var discovery = await client.GetFromJsonAsync<JsonElement>(".well-known/openid-configuration", Ct);
        Assert.Equal(IdpFactory.Issuer, discovery.GetProperty("issuer").GetString());
        Assert.EndsWith("/connect/authorize", discovery.GetProperty("authorization_endpoint").GetString());
        Assert.EndsWith("/connect/token", discovery.GetProperty("token_endpoint").GetString());
        Assert.EndsWith("/connect/revoke", discovery.GetProperty("revocation_endpoint").GetString());

        // 各エンドポイントの URL は issuer ではなくリクエストの host から組み立てられる(issuer だけが固定)
        var jwksUri = new Uri(discovery.GetProperty("jwks_uri").GetString()!);
        Assert.Equal("/.well-known/jwks", jwksUri.AbsolutePath);
        var jwks = await client.GetFromJsonAsync<JsonElement>(jwksUri.PathAndQuery, Ct);
        var key = Assert.Single(jwks.GetProperty("keys").EnumerateArray());

        using var cer = LoadCer();
        var parameters = cer.GetRSAPublicKey()!.ExportParameters(false);
        Assert.Equal(Base64UrlEncoder.Encode(parameters.Modulus!), key.GetProperty("n").GetString());
        Assert.Equal(Base64UrlEncoder.Encode(parameters.Exponent!), key.GetProperty("e").GetString());
        Assert.Equal(DecodePart(token.AccessToken, 0).GetProperty("kid").GetString(),
            key.GetProperty("kid").GetString());
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
    public async Task OpenIdScope_IsNotSupported_AndRejectedAsInvalidScope()
    {
        // openid を許すと、OpenIddict.Client が id_token を必須にしてクライアントがログインできなくなる
        var id = NewDiscordId();
        _factory.Lookup.SetMember(id, "judy");
        using var client = _factory.CreateIdpClient();

        var discovery = await client.GetFromJsonAsync<JsonElement>(".well-known/openid-configuration", Ct);
        Assert.Equal(["offline_access"],
            discovery.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()));

        var response = await SendAuthorizeAsync(client, id, "openid offline_access", "S256");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_scope", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task PkcePlain_IsRejected_AndDiscoveryAdvertisesS256Only()
    {
        var id = NewDiscordId();
        _factory.Lookup.SetMember(id, "kate");
        using var client = _factory.CreateIdpClient();

        var discovery = await client.GetFromJsonAsync<JsonElement>(".well-known/openid-configuration", Ct);
        Assert.Equal(["S256"],
            discovery.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(e => e.GetString()));

        var response = await SendAuthorizeAsync(client, id, "offline_access", "plain");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_request", await response.Content.ReadAsStringAsync(Ct));
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

    [Fact]
    public async Task ClientRegistration_OverwritesStaleDescriptorInRealDb()
    {
        using var client = _factory.CreateIdpClient();
        await client.GetAsync("healthz", Ct); // 起動(=登録)させる

        await using var scope = _factory.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = (await manager.FindByClientIdAsync(ClientId, Ct))!;

        // 古い記述子(リダイレクト URI が1つだけ、revocation 権限なし)で上書きしておく
        var stale = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(stale, application, Ct);
        stale.RedirectUris.Clear();
        stale.RedirectUris.Add(new Uri("http://localhost:51180/"));
        stale.Permissions.Remove(OpenIddictConstants.Permissions.Endpoints.Revocation);
        await manager.UpdateAsync(application, stale, Ct);
        var before = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(before, (await manager.FindByClientIdAsync(ClientId, Ct))!, Ct);
        Assert.Single(before.RedirectUris);
        Assert.DoesNotContain(OpenIddictConstants.Permissions.Endpoints.Revocation, before.Permissions);

        try
        {
            await scope.ServiceProvider.GetRequiredService<ClientApplicationRegistrar>().EnsureRegisteredAsync(Ct);

            var after = new OpenIddictApplicationDescriptor();
            await manager.PopulateAsync(after, (await manager.FindByClientIdAsync(ClientId, Ct))!, Ct);
            var expected = scope.ServiceProvider.GetRequiredService<ClientApplicationRegistrar>().CreateDescriptor();
            Assert.Equal(expected.RedirectUris.Select(u => u.AbsoluteUri).Order(),
                after.RedirectUris.Select(u => u.AbsoluteUri).Order());
            Assert.Equal(expected.Permissions.Order(), after.Permissions.Order());
            Assert.Equal(expected.Requirements.Order(), after.Requirements.Order());
            Assert.Equal(5, after.RedirectUris.Count);
        }
        finally
        {
            // 他のテストに影響しないよう、最新の記述子に戻す
            await scope.ServiceProvider.GetRequiredService<ClientApplicationRegistrar>().EnsureRegisteredAsync(Ct);
        }
    }

    [Fact]
    public async Task Healthz_ReflectsBotReadiness()
    {
        using var client = _factory.CreateIdpClient();

        var ok = await client.GetAsync("healthz", Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        _factory.Lookup.IsReady = false;
        try
        {
            var notReady = await client.GetAsync("healthz", Ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        }
        finally
        {
            _factory.Lookup.IsReady = true;
        }
    }

    [Fact]
    public async Task Startup_CreatesSigningPfxAndMatchingCer()
    {
        using var client = _factory.CreateIdpClient();
        await client.GetAsync("healthz", Ct); // 起動させる

        var pfxPath = Path.Combine(_factory.CertificateDirectory, "server-signing-certificate.pfx");
        Assert.True(File.Exists(pfxPath));
        using var pfx = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, string.Empty);
        using var cer = LoadCer();
        Assert.Equal("CN=Tatehama Shukkindai Server Signing Certificate", cer.Subject);
        Assert.Equal(pfx.GetPublicKey(), cer.GetPublicKey());
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
