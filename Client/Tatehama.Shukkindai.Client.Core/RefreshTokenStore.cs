using System.Security.Cryptography;
using System.Text;
using Tatehama.Shukkindai.TokenFile;

namespace Tatehama.Shukkindai.Client.Core;

/// <summary>refresh token を ITokenProtector で暗号化して保存する。他のアプリには渡さない。</summary>
public sealed class RefreshTokenStore(ITokenProtector protector, string path)
{
    public void Save(string refreshToken)
    {
        AtomicFile.Write(path, protector.Protect(Encoding.UTF8.GetBytes(refreshToken)));
    }

    /// <summary>無い・壊れている・復号できないときは null。</summary>
    public string? Load()
    {
        try
        {
            var token = Encoding.UTF8.GetString(protector.Unprotect(File.ReadAllBytes(path)));
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException
                                      or ArgumentException)
        {
            return null;
        }
    }

    public void Delete()
    {
        AtomicFile.Delete(path);
    }
}
