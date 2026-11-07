// 出勤台の証明書(pfx)を、アプリと同じレイアウトで作る。手動ローテーションの手順書(Docs/certificate-rotation.md)用。
// 使い方: dotnet run Server/tools/gen-cert.cs -- <purpose> <certDir> [notBefore(ISO 8601。省略時は 5 分前)] [years=2]
//   purpose: server-signing | server-encryption | client-signing | client-encryption
// 作るのは pfx だけ(パスワードなし、0600)。署名用の .cer は、出勤台が起動したときに cert/public/ へ書く。
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: gen-cert.cs <purpose> <certDir> [notBefore] [years]");
    return 1;
}

var purpose = args[0];
var certDir = args[1];
var notBefore = args.Length > 2
    ? DateTimeOffset.Parse(args[2], null, System.Globalization.DateTimeStyles.AssumeUniversal)
    : DateTimeOffset.UtcNow.AddMinutes(-5);
var years = args.Length > 3 ? int.Parse(args[3]) : 2;
var (subject, usage) = purpose switch
{
    "server-signing" => ("CN=Tatehama Shukkindai Server Signing Certificate", X509KeyUsageFlags.DigitalSignature),
    "server-encryption" => ("CN=Tatehama Shukkindai Server Encryption Certificate", X509KeyUsageFlags.KeyEncipherment),
    "client-signing" => ("CN=Tatehama Shukkindai Client Signing Certificate", X509KeyUsageFlags.DigitalSignature),
    "client-encryption" => ("CN=Tatehama Shukkindai Client Encryption Certificate", X509KeyUsageFlags.KeyEncipherment),
    _ => throw new ArgumentException("unknown purpose: " + purpose),
};

using var rsa = RSA.Create(2048);
var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
using var certificate = request.CreateSelfSigned(notBefore, notBefore.AddYears(years));

var directory = Path.Combine(certDir, purpose);
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, $"{notBefore.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{certificate.Thumbprint[..8]}.pfx");
var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
if (!OperatingSystem.IsWindows())
{
    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
}

using (var stream = new FileStream(path, options))
{
    stream.Write(certificate.Export(X509ContentType.Pfx, string.Empty));
}

Console.WriteLine(path);
Console.WriteLine($"  kid (thumbprint) = {certificate.Thumbprint}");
Console.WriteLine($"  notBefore        = {certificate.NotBefore.ToUniversalTime():O}");
Console.WriteLine($"  notAfter         = {certificate.NotAfter.ToUniversalTime():O}");
return 0;
