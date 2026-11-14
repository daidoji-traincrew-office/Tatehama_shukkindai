namespace Tatehama.Shukkindai.TokenFile;

public sealed class AccessTokenFileWriter(ITokenProtector protector, string path)
{
    public void Write(string accessToken, DateTimeOffset expiresAt)
    {
        var json = AccessTokenFile.Create(accessToken, expiresAt).ToJsonBytes();
        AtomicFile.Write(path, protector.Protect(json));
    }

    public void Delete()
    {
        AtomicFile.Delete(path);
    }
}
