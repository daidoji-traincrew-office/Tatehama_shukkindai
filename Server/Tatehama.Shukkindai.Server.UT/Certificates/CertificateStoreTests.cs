using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Tatehama.Shukkindai.Server.Certificates;

namespace Tatehama.Shukkindai.Server.UT.Certificates;

public sealed class CertificateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "shukkindai-ut-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void LoadServerSigningCertificate_CreatesPfxAndPublicOnlyCer()
    {
        using var certificate = CertificateStore.LoadServerSigningCertificate(_directory);

        var pfxPath = CertificateStore.PfxPath(_directory, CertificateStore.ServerSigningCertificateName);
        var cerPath = CertificateStore.CerPath(_directory, CertificateStore.ServerSigningCertificateName);
        Assert.True(File.Exists(pfxPath));
        Assert.True(File.Exists(cerPath));

        using var pfx = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, string.Empty);
        using var cer = X509CertificateLoader.LoadCertificateFromFile(cerPath);
        Assert.True(pfx.HasPrivateKey);
        Assert.False(cer.HasPrivateKey);
        Assert.Equal(pfx.GetPublicKey(), cer.GetPublicKey());
        Assert.Equal("CN=Tatehama Shukkindai Server Signing Certificate", cer.Subject);
        Assert.InRange(cer.NotAfter, DateTime.Now.AddYears(2).AddDays(-1), DateTime.Now.AddYears(2).AddDays(1));
    }

    [Fact]
    public void SecondLoad_DoesNotRecreate()
    {
        using var first = CertificateStore.LoadServerSigningCertificate(_directory);
        var pfxPath = CertificateStore.PfxPath(_directory, CertificateStore.ServerSigningCertificateName);
        var cerPath = CertificateStore.CerPath(_directory, CertificateStore.ServerSigningCertificateName);
        var pfxBytes = File.ReadAllBytes(pfxPath);
        var cerBytes = File.ReadAllBytes(cerPath);

        using var second = CertificateStore.LoadServerSigningCertificate(_directory);

        Assert.Equal(pfxBytes, File.ReadAllBytes(pfxPath));
        Assert.Equal(cerBytes, File.ReadAllBytes(cerPath));
        Assert.Equal(first.Thumbprint, second.Thumbprint);
    }

    [Fact]
    public void MissingCer_IsWrittenAgainFromExistingPfx()
    {
        using var first = CertificateStore.LoadServerSigningCertificate(_directory);
        var cerPath = CertificateStore.CerPath(_directory, CertificateStore.ServerSigningCertificateName);
        File.Delete(cerPath);

        using var second = CertificateStore.LoadServerSigningCertificate(_directory);

        Assert.Equal(first.Thumbprint, second.Thumbprint);
        using var cer = X509CertificateLoader.LoadCertificateFromFile(cerPath);
        Assert.Equal(first.Thumbprint, cer.Thumbprint);
    }

    [Fact]
    public void RecreatedPfx_RewritesStaleCer()
    {
        using var first = CertificateStore.LoadServerSigningCertificate(_directory);
        var pfxPath = CertificateStore.PfxPath(_directory, CertificateStore.ServerSigningCertificateName);
        var cerPath = CertificateStore.CerPath(_directory, CertificateStore.ServerSigningCertificateName);
        var oldCer = File.ReadAllBytes(cerPath);

        File.Delete(pfxPath);
        using var second = CertificateStore.LoadServerSigningCertificate(_directory);

        Assert.NotEqual(first.Thumbprint, second.Thumbprint);
        var newCer = File.ReadAllBytes(cerPath);
        Assert.NotEqual(oldCer, newCer);
        using var cer = X509CertificateLoader.LoadCertificate(newCer);
        Assert.Equal(second.Thumbprint, cer.Thumbprint);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void UnixPermissions_PfxIs0600_DirectoryIs0700_CerIs0644()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix のファイルモードは Windows にない");
        var directory = Path.Combine(_directory, "nested");

        using var certificate = CertificateStore.LoadServerSigningCertificate(directory);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(directory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(
            CertificateStore.PfxPath(directory, CertificateStore.ServerSigningCertificateName)));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            File.GetUnixFileMode(CertificateStore.CerPath(directory, CertificateStore.ServerSigningCertificateName)));
        Assert.False(CertificateStore.IsPermissionTooOpen(
            CertificateStore.PfxPath(directory, CertificateStore.ServerSigningCertificateName)));

        var pfx = CertificateStore.PfxPath(directory, CertificateStore.ServerSigningCertificateName);
        File.SetUnixFileMode(pfx, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.True(CertificateStore.IsPermissionTooOpen(pfx));
    }

    [Fact]
    public void WarnIfExpiring_WarnsOnlyForExpiredOrNearExpiry()
    {
        using var certificate = CertificateStore.LoadServerEncryptionCertificate(_directory);
        var logger = new ListLogger();
        var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());

        CertificateStore.WarnIfExpiring([("x", certificate)], logger, notAfter.AddYears(-1));
        Assert.Empty(logger.Warnings);

        CertificateStore.WarnIfExpiring([("x", certificate)], logger, notAfter.AddDays(-10));
        Assert.Single(logger.Warnings);

        CertificateStore.WarnIfExpiring([("x", certificate)], logger, notAfter.AddDays(1));
        Assert.Equal(2, logger.Warnings.Count);
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
