namespace Tatehama.Shukkindai.TokenFile;

public static class TokenFilePaths
{
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tatehama.Shukkindai");

    public static string AccessToken { get; } = Path.Combine(Directory, "access-token.bin");

    public static string RefreshToken { get; } = Path.Combine(Directory, "refresh-token.bin");
}
