#:property NoWarn=CA1416
// 出勤台の証明書(pfx)を、アプリと同じレイアウトで作る。手動ローテーションの手順書(Docs/certificate-rotation.md)用。
// 使い方: dotnet run Server/tools/gen-cert.cs -- <purpose> <certDir> [notBefore(ISO 8601。省略時は 5 分前)] [days=730]
//   purpose: server-signing | server-encryption | client-signing | client-encryption
// 作るのは pfx だけ(パスワードなし、0600)。署名用の .cer は、出勤台が起動したときに cert/public/ へ書く。
// アプリと同じく、用途のディレクトリは 0700、書き込みは一時ファイル + move、cert/.rotation.lock の排他ロックの中で行う。
// 出勤台のコンテナのユーザー(UID 1654)で実行すること(root で作ると、出勤台が読めない)。
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
var days = args.Length > 3 ? int.Parse(args[3]) : 730; // アプリと同じ寿命(2 年 = 730 日)
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
using var certificate = request.CreateSelfSigned(notBefore, notBefore.AddDays(days));

var privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
void EnsureDirectory(string path)
{
    if (Directory.Exists(path))
    {
        return;
    }

    Directory.CreateDirectory(path, privateDirectory);
    File.SetUnixFileMode(path, privateDirectory); // umask で削られた分を戻す
}

EnsureDirectory(certDir);
var directory = Path.Combine(certDir, purpose);
EnsureDirectory(directory);

// 出勤台と同じロック。出勤台が同時にローテーションしていないときに作ること
using var rotationLock = new FileStream(Path.Combine(certDir, ".rotation.lock"), new FileStreamOptions
{
    Mode = FileMode.OpenOrCreate,
    Access = FileAccess.ReadWrite,
    Share = FileShare.None,
    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
});

var path = Path.Combine(directory, $"{notBefore.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{certificate.Thumbprint[..8]}.pfx");
var temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
try
{
    using (var stream = new FileStream(temporaryPath, new FileStreamOptions
           {
               Mode = FileMode.CreateNew,
               Access = FileAccess.Write,
               UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
           }))
    {
        stream.Write(certificate.Export(X509ContentType.Pfx, string.Empty));
        stream.Flush(flushToDisk: true);
    }

    File.Move(temporaryPath, path, overwrite: false);
}
catch
{
    File.Delete(temporaryPath);
    throw;
}

Console.WriteLine(path);
Console.WriteLine($"  kid (thumbprint) = {certificate.Thumbprint}");
Console.WriteLine($"  notBefore        = {certificate.NotBefore.ToUniversalTime():O}");
Console.WriteLine($"  notAfter         = {certificate.NotAfter.ToUniversalTime():O}");
return 0;
