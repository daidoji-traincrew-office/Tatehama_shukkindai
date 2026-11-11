using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Tatehama.Shukkindai.TokenFile;

/// <summary>
/// DPAPI(現在の Windows ユーザー)で暗号化する。LocalMachine は使わない。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenProtector : ITokenProtector
{
    public byte[] Protect(byte[] data)
    {
        return ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] data)
    {
        return ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
    }
}
