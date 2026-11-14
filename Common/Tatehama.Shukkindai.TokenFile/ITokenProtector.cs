namespace Tatehama.Shukkindai.TokenFile;

public interface ITokenProtector
{
    byte[] Protect(byte[] data);

    byte[] Unprotect(byte[] data);
}
