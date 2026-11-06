using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Tatehama.Shukkindai.Server.Certificates;

/// <summary>
/// 自己署名証明書(pfx)の生成と読み込み。自動ローテーションはしない。
/// 署名用証明書は、Crew と TTC が静的に検証するための公開鍵だけの .cer も書き出す。
/// </summary>
public static class CertificateStore
{
    public const string ServerSigningCertificateName = "server-signing-certificate";
    public const string ServerEncryptionCertificateName = "server-encryption-certificate";
    public const string ClientSigningCertificateName = "client-signing-certificate";
    public const string ClientEncryptionCertificateName = "client-encryption-certificate";

    public static readonly TimeSpan ExpirationWarningPeriod = TimeSpan.FromDays(30);

    public static string PfxPath(string directory, string name) => Path.Combine(directory, name + ".pfx");
    public static string CerPath(string directory, string name) => Path.Combine(directory, name + ".cer");

    /// <summary>pfx が無ければ作る(RSA 2048、有効期限2年、パスワードなし)。作ったら true。</summary>
    public static bool EnsureCertificateExists(string certificatePath, string subjectName,
        X509KeyUsageFlags keyUsageFlags)
    {
        if (File.Exists(certificatePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(certificatePath))!;
        Directory.CreateDirectory(directory);

        using var algorithm = RSA.Create(2048);
        var request = new CertificateRequest(new X500DistinguishedName(subjectName), algorithm,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(keyUsageFlags, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(2));
        File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx, string.Empty));
        return true;
    }

    /// <summary>pfx を(無ければ作って)読み込む。</summary>
    public static X509Certificate2 LoadOrCreate(string directory, string name, string subjectName,
        X509KeyUsageFlags keyUsageFlags)
    {
        var path = PfxPath(directory, name);
        EnsureCertificateExists(path, subjectName, keyUsageFlags);
        return X509CertificateLoader.LoadPkcs12FromFile(path, string.Empty);
    }

    /// <summary>サーバーの署名用証明書を読み込み、公開鍵だけの .cer(DER)が無ければ書き出す。</summary>
    public static X509Certificate2 LoadServerSigningCertificate(string directory)
    {
        var certificate = LoadOrCreate(directory, ServerSigningCertificateName,
            "CN=Tatehama Shukkindai Server Signing Certificate", X509KeyUsageFlags.DigitalSignature);
        var cerPath = CerPath(directory, ServerSigningCertificateName);
        if (!File.Exists(cerPath))
        {
            // X509ContentType.Cert は公開鍵だけの DER
            File.WriteAllBytes(cerPath, certificate.Export(X509ContentType.Cert));
        }

        return certificate;
    }

    public static X509Certificate2 LoadServerEncryptionCertificate(string directory) =>
        LoadOrCreate(directory, ServerEncryptionCertificateName,
            "CN=Tatehama Shukkindai Server Encryption Certificate", X509KeyUsageFlags.KeyEncipherment);

    public static X509Certificate2 LoadClientSigningCertificate(string directory) =>
        LoadOrCreate(directory, ClientSigningCertificateName,
            "CN=Tatehama Shukkindai Client Signing Certificate", X509KeyUsageFlags.DigitalSignature);

    public static X509Certificate2 LoadClientEncryptionCertificate(string directory) =>
        LoadOrCreate(directory, ClientEncryptionCertificateName,
            "CN=Tatehama Shukkindai Client Encryption Certificate", X509KeyUsageFlags.KeyEncipherment);

    /// <summary>期限切れ、または期限が近い証明書の警告ログを出す。</summary>
    public static void WarnIfExpiring(IEnumerable<(string Name, X509Certificate2 Certificate)> certificates,
        ILogger logger, DateTimeOffset now)
    {
        foreach (var (name, certificate) in certificates)
        {
            var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
            if (notAfter <= now)
            {
                logger.LogWarning("Certificate {CertificateName} expired at {NotAfter:O}.", name, notAfter);
            }
            else if (notAfter - now <= ExpirationWarningPeriod)
            {
                logger.LogWarning("Certificate {CertificateName} expires at {NotAfter:O}.", name, notAfter);
            }
        }
    }
}
