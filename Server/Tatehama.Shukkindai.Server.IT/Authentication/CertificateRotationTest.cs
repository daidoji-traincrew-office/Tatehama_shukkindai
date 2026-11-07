using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Tatehama.Shukkindai.Server.IT.Collection;
using Tatehama.Shukkindai.Server.IT.Fixture;
using Tatehama.Shukkindai.Server.IT.TestUtilities;
using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Services;
using static Tatehama.Shukkindai.Server.IT.TestUtilities.IdpTestHelper;

namespace Tatehama.Shukkindai.Server.IT.Authentication;

/// <summary>
/// 証明書の自動ローテーション。ファイルは一時ディレクトリ、時刻は FakeTimeProvider(OpenIddict と同じ時計)で、
/// 「プロセスの再起動」はホスト(factory)を作り直して表す。
/// </summary>
[Collection(WebApplicationCollection.Name)]
public sealed class CertificateRotationTest(WebApplicationFixture fixture)
{
    private static async Task<string> LoginKid(HttpClient client, WebApplicationFixture fixture, string name)
    {
        var id = NewDiscordId();
        fixture.DiscordRepository.SetMember(id, name);
        var token = await Login(client, id);
        return DecodePart(token.AccessToken, 0).GetProperty("kid").GetString()!;
    }

    [Fact]
    public async Task Startup_FreshDirectory_CreatesAllCertificatesAndRestartKeepsThem()
    {
        using var env = new CertificateTestEnvironment();
        string kid;
        using (var factory = env.CreateFactory(fixture))
        {
            using var client = factory.CreateIdpClient();
            kid = await LoginKid(client, fixture, "alice");
            Assert.Equal([kid], await CertificateTestEnvironment.JwksKids(client));
        }

        // 再起動しても、増えない・変わらない
        using (var factory = env.CreateFactory(fixture))
        {
            using var client = factory.CreateIdpClient();
            Assert.Equal(kid, await LoginKid(client, fixture, "alice"));
        }

        foreach (var name in new[] { "server-signing", "server-encryption", "client-signing", "client-encryption" })
        {
            Assert.Equal(1, env.PfxCount(name));
        }

        Assert.Equal([kid], env.PublicThumbprints());
    }

    [Fact]
    public async Task NextKey_NotYetValid_IsPublishedInJwksAndPublicDirectoryButNotUsedForSigning()
    {
        using var env = new CertificateTestEnvironment();
        var old = env.Seed(CertificatePurpose.ServerSigning, -600, 100);
        var next = env.Seed(CertificatePurpose.ServerSigning, 40, 770);

        using var factory = env.CreateFactory(fixture);
        using var client = factory.CreateIdpClient();
        var kid = await LoginKid(client, fixture, "bob");

        Assert.Equal(old.Thumbprint, kid);
        Assert.Equal(new[] { old.Thumbprint, next.Thumbprint }.Order(), await CertificateTestEnvironment.JwksKids(client));
        Assert.Equal(new[] { old.Thumbprint, next.Thumbprint }.Order(), env.PublicThumbprints());
    }

    [Fact]
    public async Task NextKey_TimeAdvancesPastNotBefore_SwitchesOnlyAfterRestart()
    {
        using var env = new CertificateTestEnvironment();
        var old = env.Seed(CertificatePurpose.ServerSigning, -600, 100);
        var next = env.Seed(CertificatePurpose.ServerSigning, 40, 770);

        using (var factory = env.CreateFactory(fixture))
        {
            using var client = factory.CreateIdpClient();
            Assert.Equal(old.Thumbprint, await LoginKid(client, fixture, "carol"));

            env.Time.Advance(TimeSpan.FromDays(41));
            var service = factory.Services.GetRequiredService<ICertificateService>();
            service.Maintain();

            // 動いているプロセスは、options を作り直さず、古い鍵で署名し続ける。再起動が必要なことだけが分かる
            Assert.Equal(old.Thumbprint, await LoginKid(client, fixture, "carol"));
            var signing = service.GetStatus().Purposes.Single(p => p.Purpose == CertificatePurpose.ServerSigning);
            Assert.True(CertificateRotationPolicy.IsRestartRequired(signing, env.Now));
            // 切り替わった後も、古い鍵の署名は検証できる(.cer も JWKS も両方残っている)
            Assert.Equal(new[] { old.Thumbprint, next.Thumbprint }.Order(), env.PublicThumbprints());
        }

        // 再起動 → 次の鍵で署名される。古い鍵も、期限まで JWKS と public/ に残る
        using (var factory = env.CreateFactory(fixture))
        {
            using var client = factory.CreateIdpClient();
            Assert.Equal(next.Thumbprint, await LoginKid(client, fixture, "carol"));
            Assert.Equal(new[] { old.Thumbprint, next.Thumbprint }.Order(), await CertificateTestEnvironment.JwksKids(client));
            var service = factory.Services.GetRequiredService<ICertificateService>();
            var signing = service.GetStatus().Purposes.Single(p => p.Purpose == CertificatePurpose.ServerSigning);
            Assert.False(CertificateRotationPolicy.IsRestartRequired(signing, env.Now));
        }
    }

    [Fact]
    public async Task Startup_SigningRemaining150Days_CreatesNextKeyThatIsPublishedBeforeItIsUsed()
    {
        using var env = new CertificateTestEnvironment();
        var old = env.Seed(CertificatePurpose.ServerSigning, -580, 150);

        using var factory = env.CreateFactory(fixture);
        using var client = factory.CreateIdpClient();

        Assert.Equal(old.Thumbprint, await LoginKid(client, fixture, "dave"));
        var kids = await CertificateTestEnvironment.JwksKids(client);
        Assert.Equal(2, kids.Count);
        Assert.Equal(kids, env.PublicThumbprints());
        var next = env.Repository.GetAll(CertificatePurpose.ServerSigning).Single(c => c.Thumbprint != old.Thumbprint);
        Assert.Equal(old.NotAfter.AddDays(-60), next.NotBefore); // 切り替えは期限の 60 日前
    }

    [Fact]
    public async Task Startup_AllSigningCertificatesExpired_StopsStartup()
    {
        using var env = new CertificateTestEnvironment();
        env.Seed(CertificatePurpose.ServerSigning, -900, -10);

        using var factory = env.CreateFactory(fixture);
        var exception = Record.Exception(() => factory.CreateIdpClient());

        Assert.NotNull(exception);
        Assert.Contains("有効なサーバー署名証明書がありません", exception.ToString());
        // 黙って新しい鍵を作って起動することはしない
        Assert.Equal(1, env.PfxCount("server-signing"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Refresh_TokenEncryptedWithOldKeyWithinRetention_StillWorksAfterNewKeyIsAdded()
    {
        using var env = new CertificateTestEnvironment();
        var id = NewDiscordId();
        fixture.DiscordRepository.SetMember(id, "erin");
        string refreshToken;
        using (var factory = env.CreateFactory(fixture))
        {
            using var client = factory.CreateIdpClient();
            refreshToken = (await Login(client, id)).RefreshToken!;
        }

        // 暗号化鍵が更新された(より遠い NotAfter)。古い鍵は保持期間内なので残っている
        env.Repository.Create(CertificatePurpose.ServerEncryption, env.Now.AddMinutes(-5), env.Now.AddDays(1000));
        Assert.Equal(2, env.PfxCount("server-encryption"));

        using (var factory = env.CreateFactory(fixture))
        {
            using var client = factory.CreateIdpClient();
            var refreshed = await ReadToken(await Refresh(client, refreshToken));
            Assert.False(string.IsNullOrEmpty(refreshed.RefreshToken));
        }
    }

    [Fact]
    public async Task Refresh_TokenEncryptedWithRemovedKey_IsInvalidGrant()
    {
        using var env = new CertificateTestEnvironment();
        var id = NewDiscordId();
        fixture.DiscordRepository.SetMember(id, "frank");
        string refreshToken;
        using (var factory = env.CreateFactory(fixture))
        {
            using var client = factory.CreateIdpClient();
            refreshToken = (await Login(client, id)).RefreshToken!;
        }

        // 古い鍵を保持期間より前に消してしまうと、その鍵で暗号化された refresh token は使えない(再ログインになる)
        foreach (var certificate in env.Repository.GetAll(CertificatePurpose.ServerEncryption))
        {
            env.Repository.Delete(certificate);
        }

        env.Repository.Create(CertificatePurpose.ServerEncryption, env.Now.AddMinutes(-5), env.Now.AddDays(1000));
        using (var factory = env.CreateFactory(fixture))
        {
            using var client = factory.CreateIdpClient();
            var response = await Refresh(client, refreshToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal("invalid_grant", json.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Startup_ExpiredSigningKeyWithinRetention_IsKeptInPublicDirectoryButNotInJwks()
    {
        using var env = new CertificateTestEnvironment();
        var expired = env.Seed(CertificatePurpose.ServerSigning, -900, -3);
        var valid = env.Seed(CertificatePurpose.ServerSigning, -100, 630);

        using var factory = env.CreateFactory(fixture);
        using var client = factory.CreateIdpClient();

        Assert.Equal(valid.Thumbprint, await LoginKid(client, fixture, "grace"));
        Assert.Equal([valid.Thumbprint], await CertificateTestEnvironment.JwksKids(client));
        Assert.Equal(new[] { expired.Thumbprint, valid.Thumbprint }.Order(), env.PublicThumbprints());
    }
}
