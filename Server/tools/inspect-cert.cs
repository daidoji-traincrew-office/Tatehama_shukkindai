// pfx / cer を読み、kid(thumbprint)と期限を表示する。手動ローテーションの手順書用。
// 使い方: dotnet run Server/tools/inspect-cert.cs -- <file>...
using System.Security.Cryptography.X509Certificates;

foreach (var path in args)
{
    using var certificate = path.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
        ? X509CertificateLoader.LoadPkcs12FromFile(path, string.Empty)
        : X509CertificateLoader.LoadCertificateFromFile(path);
    var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault()?.KeyUsages;
    var remaining = (certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays;
    Console.WriteLine($"{Path.GetFileName(path)}: kid={certificate.Thumbprint} keyUsage={usage} " +
                      $"hasPrivateKey={certificate.HasPrivateKey} notBefore={certificate.NotBefore.ToUniversalTime():O} " +
                      $"notAfter={certificate.NotAfter.ToUniversalTime():O} remainingDays={remaining:F0}");
}
