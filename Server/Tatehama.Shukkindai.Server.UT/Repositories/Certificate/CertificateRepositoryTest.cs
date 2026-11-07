using System.Security.Cryptography.X509Certificates;
using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Repositories.Certificate;
using Tatehama.Shukkindai.Server.UT.TestUtilities;

namespace Tatehama.Shukkindai.Server.UT.Repositories.Certificate;

public sealed class CertificateRepositoryTest : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _directory = new();
    private readonly CertificateRepository _repository;

    public CertificateRepositoryTest()
    {
        _repository = new CertificateRepository(_directory.Path);
    }

    public void Dispose() => _directory.Dispose();

    private string PurposeDirectory(string name) => Path.Combine(_directory.Path, name);
    private string PublicDirectory => Path.Combine(_directory.Path, "public");

    [Fact]
    public void Create_Signing_WritesPfxWithMetadataAndPrivateKey()
    {
        var info = _repository.Create(CertificatePurpose.ServerSigning, Now.AddDays(-1), Now.AddDays(729));

        Assert.Equal(CertificatePurpose.ServerSigning, info.Purpose);
        Assert.Equal(Now.AddDays(-1), info.NotBefore);
        Assert.Equal(Now.AddDays(729), info.NotAfter);
        Assert.Equal(PurposeDirectory("server-signing"), Path.GetDirectoryName(info.Path));
        Assert.Matches(@"^20261106T000000Z-[0-9A-F]{8}\.pfx$", Path.GetFileName(info.Path));
        using var loaded = _repository.Load(info);
        Assert.True(loaded.HasPrivateKey);
        Assert.Equal(info.Thumbprint, loaded.Thumbprint);
        Assert.Equal("CN=Tatehama Shukkindai Server Signing Certificate", loaded.Subject);
    }

    [Theory]
    [InlineData(CertificatePurpose.ServerSigning, "server-signing", X509KeyUsageFlags.DigitalSignature)]
    [InlineData(CertificatePurpose.ServerEncryption, "server-encryption", X509KeyUsageFlags.KeyEncipherment)]
    [InlineData(CertificatePurpose.ClientSigning, "client-signing", X509KeyUsageFlags.DigitalSignature)]
    [InlineData(CertificatePurpose.ClientEncryption, "client-encryption", X509KeyUsageFlags.KeyEncipherment)]
    public void Create_Purpose_UsesPurposeDirectoryAndKeyUsage(
        CertificatePurpose purpose, string directoryName, X509KeyUsageFlags usage)
    {
        var info = _repository.Create(purpose, Now, Now.AddDays(10));

        Assert.Equal(PurposeDirectory(directoryName), Path.GetDirectoryName(info.Path));
        using var loaded = _repository.Load(info);
        Assert.Equal(usage, loaded.Extensions.OfType<X509KeyUsageExtension>().Single().KeyUsages);
    }

    [Fact]
    public void Create_OnUnix_UsesRestrictivePermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var info = _repository.Create(CertificatePurpose.ServerSigning, Now, Now.AddDays(10));

        Assert.Equal((UnixFileMode)0b110_000_000, File.GetUnixFileMode(info.Path)); // 0600
        Assert.Equal((UnixFileMode)0b111_000_000, File.GetUnixFileMode(PurposeDirectory("server-signing"))); // 0700
        Assert.Equal((UnixFileMode)0b111_000_000, File.GetUnixFileMode(_directory.Path)); // 0700
    }

    [Fact]
    public void Create_Always_LeavesNoTemporaryFile()
    {
        _repository.Create(CertificatePurpose.ServerEncryption, Now, Now.AddDays(10));

        Assert.Empty(Directory.GetFiles(PurposeDirectory("server-encryption"), "*.tmp"));
        Assert.Empty(Directory.GetFiles(PurposeDirectory("server-encryption"), ".*"));
    }

    [Fact]
    public void GetAll_DirectoryMissing_ReturnsEmpty()
    {
        Assert.Empty(_repository.GetAll(CertificatePurpose.ServerSigning));
    }

    [Fact]
    public void GetAll_TemporaryAndOtherFiles_AreIgnored()
    {
        var info = _repository.Create(CertificatePurpose.ServerSigning, Now, Now.AddDays(10));
        var directory = PurposeDirectory("server-signing");
        File.WriteAllText(Path.Combine(directory, "half-written.pfx.tmp"), "partial");
        File.WriteAllText(Path.Combine(directory, ".abc.tmp"), "partial");
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "memo");

        var all = _repository.GetAll(CertificatePurpose.ServerSigning);

        Assert.Equal([info.Thumbprint], all.Select(c => c.Thumbprint));
    }

    [Fact]
    public void GetAll_BrokenPfx_ThrowsWithPathInMessage()
    {
        Directory.CreateDirectory(PurposeDirectory("server-signing"));
        var broken = Path.Combine(PurposeDirectory("server-signing"), "broken.pfx");
        File.WriteAllText(broken, "not a pfx");

        var exception = Assert.Throws<InvalidDataException>(() => _repository.GetAll(CertificatePurpose.ServerSigning));

        Assert.Contains(broken, exception.Message);
    }

    [Fact]
    public void Delete_Certificate_RemovesPfx()
    {
        var info = _repository.Create(CertificatePurpose.ServerEncryption, Now, Now.AddDays(10));

        _repository.Delete(info);

        Assert.Empty(_repository.GetAll(CertificatePurpose.ServerEncryption));
    }

    [Fact]
    public void SyncPublic_Signing_WritesPublicKeyOnlyCerFiles()
    {
        var first = _repository.Create(CertificatePurpose.ServerSigning, Now.AddDays(-10), Now.AddDays(100));
        var second = _repository.Create(CertificatePurpose.ServerSigning, Now.AddDays(40), Now.AddDays(770));

        var changes = _repository.SyncPublic([first, second]);

        Assert.Equal(2, changes.Count);
        var files = Directory.GetFiles(PublicDirectory).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(new[] { $"server-signing-{first.Thumbprint}.cer", $"server-signing-{second.Thumbprint}.cer" }.Order(),
            files);
        using var cer = X509CertificateLoader.LoadCertificateFromFile(
            Path.Combine(PublicDirectory, $"server-signing-{first.Thumbprint}.cer"));
        Assert.False(cer.HasPrivateKey);
        Assert.Equal(first.Thumbprint, cer.Thumbprint);
    }

    [Fact]
    public void SyncPublic_OnUnix_UsesWorldReadablePermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var info = _repository.Create(CertificatePurpose.ServerSigning, Now, Now.AddDays(10));

        _repository.SyncPublic([info]);

        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(PublicDirectory)); // 0755
        Assert.Equal((UnixFileMode)0b110_100_100,
            File.GetUnixFileMode(Path.Combine(PublicDirectory, $"server-signing-{info.Thumbprint}.cer"))); // 0644
    }

    [Fact]
    public void SyncPublic_CertificateRemoved_DeletesItsCerAndUnknownCerButKeepsOthers()
    {
        var keep = _repository.Create(CertificatePurpose.ServerSigning, Now.AddDays(-10), Now.AddDays(100));
        var removed = _repository.Create(CertificatePurpose.ServerSigning, Now.AddDays(-20), Now.AddDays(90));
        _repository.SyncPublic([keep, removed]);
        File.WriteAllBytes(Path.Combine(PublicDirectory, "stray.cer"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(PublicDirectory, "README.txt"), "memo");
        _repository.Delete(removed);

        var changes = _repository.SyncPublic([keep]);

        Assert.Contains($"-server-signing-{removed.Thumbprint}.cer", changes);
        Assert.Contains("-stray.cer", changes);
        Assert.Equal(new[] { "README.txt", $"server-signing-{keep.Thumbprint}.cer" }.Order(),
            Directory.GetFiles(PublicDirectory).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void SyncPublic_Unchanged_ChangesNothing()
    {
        var info = _repository.Create(CertificatePurpose.ServerSigning, Now, Now.AddDays(10));
        _repository.SyncPublic([info]);

        Assert.Empty(_repository.SyncPublic([info]));
    }

    [Fact]
    public void SyncPublic_CerContentDiffers_RewritesIt()
    {
        var info = _repository.Create(CertificatePurpose.ServerSigning, Now, Now.AddDays(10));
        _repository.SyncPublic([info]);
        var cerPath = Path.Combine(PublicDirectory, $"server-signing-{info.Thumbprint}.cer");
        File.WriteAllBytes(cerPath, [1, 2, 3]);

        var changes = _repository.SyncPublic([info]);

        Assert.Single(changes);
        using var cer = X509CertificateLoader.LoadCertificateFromFile(cerPath);
        Assert.Equal(info.Thumbprint, cer.Thumbprint);
    }

    [Fact]
    public void SyncPublic_Always_LeavesNoTemporaryFileInPublicDirectory()
    {
        var info = _repository.Create(CertificatePurpose.ServerSigning, Now, Now.AddDays(10));

        _repository.SyncPublic([info]);

        Assert.DoesNotContain(Directory.GetFiles(PublicDirectory), f => f.EndsWith(".tmp"));
    }

    [Fact]
    public void SyncPublic_TemporaryFilesOlderThanOneDay_AreRemoved()
    {
        var info = _repository.Create(CertificatePurpose.ServerSigning, Now, Now.AddDays(10));
        _repository.SyncPublic([info]);
        var stale = Path.Combine(PublicDirectory, ".stale.tmp");
        var fresh = Path.Combine(PublicDirectory, ".fresh.tmp");
        var staleInPurpose = Path.Combine(PurposeDirectory("server-signing"), ".stale.tmp");
        File.WriteAllText(stale, "x");
        File.WriteAllText(fresh, "x");
        File.WriteAllText(staleInPurpose, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(staleInPurpose, DateTime.UtcNow.AddDays(-2));

        _repository.SyncPublic([info]);

        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(staleInPurpose));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task WithLock_TwoCallers_RunOneAtATime()
    {
        var inside = 0;
        var maxInside = 0;
        var gate = new object();

        int Work()
        {
            lock (gate)
            {
                inside++;
                maxInside = Math.Max(maxInside, inside);
            }

            Thread.Sleep(150);
            lock (gate)
            {
                inside--;
            }

            return 1;
        }

        // 別の Repository(=別のインスタンスのつもり)からでも、同じファイルで排他される
        var other = new CertificateRepository(_directory.Path);
        var tasks = new[]
        {
            Task.Run(() => _repository.WithLock(Work), TestContext.Current.CancellationToken),
            Task.Run(() => other.WithLock(Work), TestContext.Current.CancellationToken),
            Task.Run(() => _repository.WithLock(Work), TestContext.Current.CancellationToken),
        };
        await Task.WhenAll(tasks);

        Assert.Equal(1, maxInside);
        Assert.True(File.Exists(Path.Combine(_directory.Path, ".rotation.lock")));
    }

    [Fact]
    public void FindLoosePermissions_OnUnix_ReportsDirectoriesAndFilesOpenToOthers()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var info = _repository.Create(CertificatePurpose.ServerSigning, Now, Now.AddDays(10));
        Assert.Empty(_repository.FindLoosePermissions());

        File.SetUnixFileMode(info.Path, (UnixFileMode)0b110_100_000);
        File.SetUnixFileMode(PurposeDirectory("server-signing"), (UnixFileMode)0b111_101_101);

        var loose = _repository.FindLoosePermissions();

        Assert.Equal(2, loose.Count);
        Assert.Contains(loose, l => l.StartsWith(info.Path));
        Assert.Contains(loose, l => l.StartsWith(PurposeDirectory("server-signing")));
    }

    [Fact]
    public async Task SyncPublic_ReplacingExistingCer_NeverLeavesTheFileMissing()
    {
        var info = _repository.Create(CertificatePurpose.ServerSigning, Now, Now.AddDays(10));
        _repository.SyncPublic([info]);
        var cerPath = Path.Combine(PublicDirectory, $"server-signing-{info.Thumbprint}.cer");
        File.WriteAllBytes(cerPath, [1, 2, 3]);
        var missing = 0;
        using var stop = new CancellationTokenSource();
        var watcher = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (!File.Exists(cerPath))
                {
                    Interlocked.Increment(ref missing);
                }
            }
        }, TestContext.Current.CancellationToken);

        for (var i = 0; i < 20; i++)
        {
            File.WriteAllBytes(cerPath, [1, 2, 3]);
            _repository.SyncPublic([info]);
        }

        stop.Cancel();
        await watcher;
        Assert.Equal(0, missing);
    }

    [Fact]
    public void IsPublished_CerWithSameContent_IsTrueOtherwiseFalse()
    {
        var info = _repository.Create(CertificatePurpose.ServerSigning, Now, Now.AddDays(10));
        Assert.False(_repository.IsPublished(info));

        _repository.SyncPublic([info]);
        Assert.True(_repository.IsPublished(info));

        File.WriteAllBytes(Path.Combine(PublicDirectory, $"server-signing-{info.Thumbprint}.cer"), [1]);
        Assert.False(_repository.IsPublished(info));
    }

    [Fact]
    public void FindUnexpectedPublicFiles_PfxAndOtherFiles_AreReportedButCerAndTmpAreNot()
    {
        var info = _repository.Create(CertificatePurpose.ServerSigning, Now, Now.AddDays(10));
        _repository.SyncPublic([info]);
        File.WriteAllText(Path.Combine(PublicDirectory, "leaked.pfx"), "x");
        File.WriteAllText(Path.Combine(PublicDirectory, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(PublicDirectory, ".abc.tmp"), "x");

        Assert.Equal(["leaked.pfx", "notes.txt"], _repository.FindUnexpectedPublicFiles());
    }

    [Fact]
    public void GetAll_OnUnixUnreadableFile_ThrowsWithReadableMessage()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var info = _repository.Create(CertificatePurpose.ServerSigning, Now, Now.AddDays(10));
        File.SetUnixFileMode(info.Path, UnixFileMode.None);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => _repository.GetAll(CertificatePurpose.ServerSigning));

            Assert.Contains("permission denied", exception.Message);
            Assert.Contains("1654", exception.Message);
        }
        finally
        {
            File.SetUnixFileMode(info.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
