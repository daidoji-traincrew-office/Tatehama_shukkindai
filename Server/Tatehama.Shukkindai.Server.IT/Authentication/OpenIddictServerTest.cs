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
    private readonly WebApplicationFixture _factory = fixture;
    private X509Certificate2 LoadCer() => X509CertificateLoader.LoadCertificateFromFile(
        Path.Combine(_factory.CertificateDirectory, "server-signing-certificate.cer"));

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
        var wrong = await new JsonWebTokenHandler().ValidateTokenAsync(token.AccessToken, new TokenValidationParameters
        {
            ValidIssuer = WebApplicationFixture.Issuer,
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
}
