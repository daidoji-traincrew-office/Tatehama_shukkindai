using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Tatehama.Shukkindai.Server.Configuration;
using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Services;

namespace Tatehama.Shukkindai.Server.Repositories.Certificate;

public class CertificateRepository : ICertificateRepository
{
    public const string PublicDirectoryName = "public";
    public const string LockFileName = ".rotation.lock";

    // Unix のファイル権限。pfx は秘密鍵を含むので所有者だけ。public/ は Crew と TTC が読む
    public const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public const UnixFileMode PfxMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public const UnixFileMode PublicDirectoryMode = PrivateDirectoryMode | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    public const UnixFileMode CerMode = PfxMode | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>ロックを待つ上限。</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan LockRetryInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>これより古い一時ファイルは、書き込み中の残骸とみなして消す。</summary>
    private static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromDays(1);

    private readonly string _root;

    public CertificateRepository(IOptions<AuthenticationSettings> settings)
        : this(settings.Value.CertificateDirectory)
    {
    }

    public CertificateRepository(string rootDirectory)
    {
        _root = Path.GetFullPath(rootDirectory);
    }

    private string PurposeDirectory(CertificatePurpose purpose) =>
        Path.Combine(_root, CertificateRotationPolicy.RuleFor(purpose).DirectoryName);

    private string PublicDirectory => Path.Combine(_root, PublicDirectoryName);

    public IReadOnlyList<CertificateInfo> GetAll(CertificatePurpose purpose)
    {
        var directory = PurposeDirectory(purpose);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<CertificateInfo>();
        foreach (var path in Directory.EnumerateFiles(directory)
                     .Where(p => string.Equals(Path.GetExtension(p), ".pfx", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            try
            {
                using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, string.Empty);
                result.Add(ToInfo(purpose, certificate, path));
            }
            catch (CryptographicException e)
            {
                throw new InvalidDataException(
                    $"Certificate file {path} could not be read as a PKCS#12 file without a password. " +
                    "Move the broken file away (see Docs/certificate-rotation.md).", e);
            }
        }

        return result;
    }

    private static CertificateInfo ToInfo(CertificatePurpose purpose, X509Certificate2 certificate, string path) =>
        new(purpose, certificate.Thumbprint,
            new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero), path);

    public X509Certificate2 Load(CertificateInfo certificate) =>
        X509CertificateLoader.LoadPkcs12FromFile(certificate.Path, string.Empty);

    public CertificateInfo Create(CertificatePurpose purpose, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var rule = CertificateRotationPolicy.RuleFor(purpose);
        var directory = PurposeDirectory(purpose);
        EnsureDirectory(_root, PrivateDirectoryMode);
        EnsureDirectory(directory, PrivateDirectoryMode);

        using var algorithm = RSA.Create(2048);
        var request = new CertificateRequest(new X500DistinguishedName(rule.Subject), algorithm,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(rule.KeyUsage, true));
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);

        var fileName = $"{notBefore.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{certificate.Thumbprint[..8]}.pfx";
        var path = Path.Combine(directory, fileName);
        WriteAtomically(path, certificate.Export(X509ContentType.Pfx, string.Empty), PfxMode, directory);
        return ToInfo(purpose, certificate, path);
    }

    public void Delete(CertificateInfo certificate) => File.Delete(certificate.Path);

    public IReadOnlyList<string> SyncPublic(IReadOnlyList<CertificateInfo> signingCertificates)
    {
        EnsureDirectory(_root, PrivateDirectoryMode);
        EnsureDirectory(PublicDirectory, PublicDirectoryMode);

        var changes = new List<string>();
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var info in signingCertificates)
        {
            using var certificate = Load(info);
            // X509ContentType.Cert は公開鍵だけの DER
            expected[CerFileName(info.Thumbprint)] = certificate.Export(X509ContentType.Cert);
        }

        foreach (var (name, bytes) in expected.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var path = Path.Combine(PublicDirectory, name);
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                continue;
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            // 一時ファイルは .tmp 拡張子にする。Crew と TTC が *.cer で読むので、書きかけを見せない
            WriteAtomically(path, bytes, CerMode, PublicDirectory);
            changes.Add("+" + name);
        }

        foreach (var path in Directory.EnumerateFiles(PublicDirectory)
                     .Where(p => string.Equals(Path.GetExtension(p), ".cer", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(path);
            if (!expected.ContainsKey(name))
            {
                File.Delete(path);
                changes.Add("-" + name);
            }
        }

        DeleteStaleTemporaryFiles();
        return changes;
    }

    public static string CerFileName(string thumbprint) => $"server-signing-{thumbprint}.cer";

    private void DeleteStaleTemporaryFiles()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        var limit = DateTime.UtcNow - StaleTemporaryFileAge;
        foreach (var directory in Directory.EnumerateDirectories(_root).Append(_root))
        {
            foreach (var path in Directory.EnumerateFiles(directory)
                         .Where(p => string.Equals(Path.GetExtension(p), ".tmp", StringComparison.OrdinalIgnoreCase)))
            {
                if (File.GetLastWriteTimeUtc(path) < limit)
                {
                    File.Delete(path);
                }
            }
        }
    }

    public T WithLock<T>(Func<T> action)
    {
        EnsureDirectory(_root, PrivateDirectoryMode);
        var lockPath = Path.Combine(_root, LockFileName);
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            FileStream? stream = null;
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = PfxMode;
                }

                stream = new FileStream(lockPath, options);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // 他のインスタンス(またはスレッド)が持っている
                Thread.Sleep(LockRetryInterval);
                continue;
            }

            using (stream)
            {
                return action();
            }
        }
    }

    public IReadOnlyList<string> FindLoosePermissions()
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(_root))
        {
            return [];
        }

        var result = new List<string>();
        const UnixFileMode group = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;
        const UnixFileMode other = UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        const UnixFileMode loose = group | other;

        void Check(string path, UnixFileMode allowed)
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & loose & ~allowed) != UnixFileMode.None)
            {
                result.Add($"{path} ({Convert.ToString((int)mode, 8)})");
            }
        }

        Check(_root, UnixFileMode.None);
        foreach (var purpose in Enum.GetValues<CertificatePurpose>())
        {
            var directory = PurposeDirectory(purpose);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            Check(directory, UnixFileMode.None);
            foreach (var path in Directory.EnumerateFiles(directory, "*.pfx"))
            {
                Check(path, UnixFileMode.None);
            }
        }

        if (File.Exists(Path.Combine(_root, LockFileName)))
        {
            Check(Path.Combine(_root, LockFileName), UnixFileMode.None);
        }

        return result;
    }

    private static void EnsureDirectory(string path, UnixFileMode mode)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, mode);
        // 作成時の権限は umask で削られる。期待どおりの権限にそろえる
        File.SetUnixFileMode(path, mode);
    }

    /// <summary>
    /// 同じディレクトリに一時ファイル(.tmp)を書いてから、move で置く。
    /// 読み手は、完成したファイルか、無いかのどちらかしか見ない。置く先が既にあれば失敗する(上書きしない)。
    /// </summary>
    private static void WriteAtomically(string path, byte[] bytes, UnixFileMode mode, string temporaryDirectory)
    {
        var temporaryPath = Path.Combine(temporaryDirectory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = mode;
            }

            using (var stream = new FileStream(temporaryPath, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, mode);
            }

            File.Move(temporaryPath, path, overwrite: false);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }
}
