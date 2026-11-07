namespace Tatehama.Shukkindai.Server.UT.TestUtilities;

/// <summary>テストごとの一時ディレクトリ。Dispose で消す。</summary>
public sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shukkindai-ut-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
    }
}
