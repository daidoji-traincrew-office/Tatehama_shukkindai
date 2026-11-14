namespace Tatehama.Shukkindai.TokenFile;

/// <summary>
/// 同じディレクトリの一時ファイルに書いてから置き換える(読む側に書きかけが見えない)。
/// </summary>
public static class AtomicFile
{
    public static void Write(string path, byte[] data)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>ファイルが無い(ディレクトリごと無い場合も)ときは何もしない。</summary>
    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
