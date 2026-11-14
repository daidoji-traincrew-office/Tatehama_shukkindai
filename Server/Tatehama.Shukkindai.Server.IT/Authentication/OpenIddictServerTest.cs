using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Tatehama.Shukkindai.Server.IT.Collection;
using Tatehama.Shukkindai.Server.IT.Fixture;
using static Tatehama.Shukkindai.Server.IT.TestUtilities.IdpTestHelper;

namespace Tatehama.Shukkindai.Server.IT.Authentication;

[Collection(WebApplicationCollection.Name)]
public sealed class OpenIddictServerTest(WebApplicationFixture fixture)
{
    /// <summary>Crew と TTC が <c>cert/public/</c> から読む .cer を全部読む。</summary>
    private X509Certificate2 LoadCer()
    {
        var path = Assert.Single(Directory.GetFiles(fixture.PublicDirectory, "*.cer"));
        return X509CertificateLoader.LoadCertificateFromFile(path);
    }

    [Fact]
    public async Task Token_AccessToken_ValidatesStaticallyWithPublicCer()
    {
        // Crew と TTC が行う静的検証そのもの
        var id = NewDiscordId();
        fixture.DiscordRepository.SetMember(id, "bob");
        using var client = fixture.CreateIdpClient();
        var token = await Login(client, id);

        using var cer = LoadCer();
        Assert.False(cer.HasPrivateKey);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.AccessToken, new()
        {
            ValidIssuer = WebApplicationFixture.Issuer,
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
        var wrong = await new JsonWebTokenHandler().ValidateTokenAsync(token.AccessToken, new()
        {
            ValidIssuer = WebApplicationFixture.Issuer,
            IssuerSigningKey = new RsaSecurityKey(other),
            ValidateAudience = false,
        });
        Assert.False(wrong.IsValid);
    }

    [Fact]
    public async Task Discovery_Always_MatchesIssuerAndJwksMatchesCer()
    {
        var id = NewDiscordId();
        fixture.DiscordRepository.SetMember(id, "carol");
        using var client = fixture.CreateIdpClient();
        var token = await Login(client, id);

        var discovery = await client.GetFromJsonAsync<JsonElement>(".well-known/openid-configuration", Ct);
        Assert.Equal(WebApplicationFixture.Issuer, discovery.GetProperty("issuer").GetString());
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
    public async Task Authorize_OpenIdScope_IsRejectedAsInvalidScope()
    {
        // openid を許すと、OpenIddict.Client が id_token を必須にしてクライアントがログインできなくなる
        var id = NewDiscordId();
        fixture.DiscordRepository.SetMember(id, "judy");
        using var client = fixture.CreateIdpClient();

        var discovery = await client.GetFromJsonAsync<JsonElement>(".well-known/openid-configuration", Ct);
        Assert.Equal(["offline_access"],
            discovery.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()));

        var response = await SendAuthorize(client, id, "openid offline_access", "S256");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_scope", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Authorize_PkcePlain_IsRejectedAndDiscoveryAdvertisesS256Only()
    {
        var id = NewDiscordId();
        fixture.DiscordRepository.SetMember(id, "kate");
        using var client = fixture.CreateIdpClient();

        var discovery = await client.GetFromJsonAsync<JsonElement>(".well-known/openid-configuration", Ct);
        Assert.Equal(["S256"],
            discovery.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(e => e.GetString()));

        var response = await SendAuthorize(client, id, "offline_access", "plain");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_request", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Startup_FreshDirectory_CreatesSigningPfxAndMatchingPublicCer()
    {
        using var client = fixture.CreateIdpClient();
        await client.GetAsync("healthz", Ct); // 起動させる

        var pfxPath = Assert.Single(Directory.GetFiles(Path.Combine(fixture.CertificateDirectory, "server-signing"),
            "*.pfx"));
        using var pfx = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, string.Empty);
        using var cer = LoadCer();
        Assert.Equal("CN=Tatehama Shukkindai Server Signing Certificate", cer.Subject);
        Assert.Equal(pfx.GetPublicKey(), cer.GetPublicKey());
        Assert.False(cer.HasPrivateKey);
        Assert.Equal($"server-signing-{cer.Thumbprint}.cer", Path.GetFileName(Directory.GetFiles(fixture.PublicDirectory)[0]));

        // 暗号化・Client 用も、初回は作る。pfx は public/ に置かない
        foreach (var name in new[] { "server-encryption", "client-signing", "client-encryption" })
        {
            Assert.Single(Directory.GetFiles(Path.Combine(fixture.CertificateDirectory, name), "*.pfx"));
        }

        Assert.DoesNotContain(Directory.GetFiles(fixture.PublicDirectory), f => f.EndsWith(".pfx"));
    }

    [Fact]
    public async Task Startup_FreshDirectory_UsesDocumentedPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var client = fixture.CreateIdpClient();
        await client.GetAsync("healthz", Ct);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(fixture.CertificateDirectory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.Combine(fixture.CertificateDirectory, "server-signing")));
        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(fixture.PublicDirectory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Directory.GetFiles(Path.Combine(fixture.CertificateDirectory, "server-signing"))[0]));
        Assert.Equal((UnixFileMode)0b110_100_100, File.GetUnixFileMode(Directory.GetFiles(fixture.PublicDirectory)[0]));
    }
}
