using Tatehama.Shukkindai.TokenFile;

namespace Tatehama.Shukkindai.Client.UT;

/// <summary>テスト用の可逆な変換(XOR)。DPAPI の代わり。</summary>
public sealed class FakeTokenProtector : ITokenProtector
{
    public byte[] Protect(byte[] data)
    {
        return Xor(data);
    }

    public byte[] Unprotect(byte[] data)
    {
        return Xor(data);
    }

    private static byte[] Xor(byte[] data)
    {
        return data.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }
}
