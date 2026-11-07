using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Tatehama.Shukkindai.Server.IT.Fixture;
using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Repositories.Certificate;

namespace Tatehama.Shukkindai.Server.IT.TestUtilities;

/// <summary>証明書のローテーションを確かめる IT の補助。一時ディレクトリと、進められる時計を使って、起動(=再起動)し直す。</summary>
public sealed class CertificateTestEnvironment : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "shukkindai-it-rotation-" + Guid.NewGuid());

    /// <summary>OpenIddict と証明書の Service が共有する時計(DI の TimeProvider)。実時間に近い時刻から始める。</summary>
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero));

    public DateTimeOffset Now => Time.GetUtcNow();

    public string PublicDirectory => Path.Combine(Directory, "public");

    public CertificateRepository Repository => new(Directory);

    public CertificateInfo Seed(CertificatePurpose purpose, double notBeforeDays, double notAfterDays) =>
        Repository.Create(purpose, Now.AddDays(notBeforeDays), Now.AddDays(notAfterDays));

    /// <summary>プロセスを起動し直したのと同じ。同じディレクトリと時計で、新しいホストを作る。</summary>
    public WebApplicationFactory<Program> CreateFactory(WebApplicationFixture fixture,
        Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? configure = null) =>
        fixture.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Authentication:CertificateDirectory", Directory);
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
            configure?.Invoke(builder);
        });

    public IReadOnlyList<string> PublicThumbprints() =>
        System.IO.Directory.Exists(PublicDirectory)
            ? System.IO.Directory.GetFiles(PublicDirectory, "*.cer")
                .Select(p => X509CertificateLoader.LoadCertificateFromFile(p).Thumbprint).Order().ToList()
            : [];

    public int PfxCount(string purposeDirectory) =>
        System.IO.Directory.GetFiles(Path.Combine(Directory, purposeDirectory), "*.pfx").Length;

    public void Dispose()
    {
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, true);
        }
    }

    public static async Task<List<string>> JwksKids(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>(".well-known/jwks", IdpTestHelper.Ct))
        .GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()!).Order().ToList();
}
