using System.Text;
using Microsoft.Extensions.Time.Testing;
using Tatehama.Shukkindai.TokenFile;

namespace Tatehama.Shukkindai.Client.UT;

public sealed class TokenFileTest : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly FakeTokenProtector _protector = new();
    private readonly FakeTimeProvider _time = new(new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero));
    private readonly string _path;

    public TokenFileTest()
    {
        _path = Path.Combine(_directory, "sub", "access-token.bin");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private AccessTokenFileReader Reader()
    {
        return new(_protector, _path, _time);
    }

    private void WriteRaw(byte[] plain)
    {
        AtomicFile.Write(_path, _protector.Protect(plain));
    }

    [Fact]
    public void Read_AfterWrite_ReturnsValidWithTokenAndExpiry()
    {
        var expiresAt = _time.GetUtcNow().AddHours(1);
        new AccessTokenFileWriter(_protector, _path).Write("dummy-access-token", expiresAt);

        var result = Reader().Read();

        var valid = Assert.IsType<AccessTokenReadResult.Valid>(result);
        Assert.Equal("dummy-access-token", valid.AccessToken);
        Assert.Equal(expiresAt, valid.ExpiresAt);
    }

    [Fact]
    public void Write_DirectoryMissing_CreatesDirectoryAndLeavesNoTemporaryFile()
    {
        new AccessTokenFileWriter(_protector, _path).Write("t", _time.GetUtcNow().AddHours(1));

        var files = Directory.GetFiles(Path.GetDirectoryName(_path)!);
        Assert.Equal([_path], files);
    }

    [Fact]
    public void Write_ExistingFile_Overwrites()
    {
        var writer = new AccessTokenFileWriter(_protector, _path);
        writer.Write("first", _time.GetUtcNow().AddHours(1));
        writer.Write("second", _time.GetUtcNow().AddHours(1));

        var valid = Assert.IsType<AccessTokenReadResult.Valid>(Reader().Read());
        Assert.Equal("second", valid.AccessToken);
    }

    [Fact]
    public void Write_Always_SerializesSnakeCaseJsonWithoutRefreshToken()
    {
        new AccessTokenFileWriter(_protector, _path).Write("tok", DateTimeOffset.FromUnixTimeSeconds(1793956743));

        var json = Encoding.UTF8.GetString(_protector.Unprotect(File.ReadAllBytes(_path)));

        Assert.Equal("{\"version\":1,\"access_token\":\"tok\",\"expires_at\":1793956743}", json);
    }

    [Fact]
    public void Read_ExpiryPassed_ReturnsExpired()
    {
        new AccessTokenFileWriter(_protector, _path).Write("t", _time.GetUtcNow().AddMinutes(1));
        _time.Advance(TimeSpan.FromMinutes(2));

        Assert.IsType<AccessTokenReadResult.Expired>(Reader().Read());
    }

    [Fact]
    public void Read_NoFile_ReturnsMissing()
    {
        Assert.IsType<AccessTokenReadResult.Missing>(Reader().Read());
    }

    [Fact]
    public void Delete_AfterWrite_ReadReturnsMissing()
    {
        var writer = new AccessTokenFileWriter(_protector, _path);
        writer.Write("t", _time.GetUtcNow().AddHours(1));

        writer.Delete();

        Assert.IsType<AccessTokenReadResult.Missing>(Reader().Read());
    }

    [Fact]
    public void Delete_NoFile_DoesNotThrow()
    {
        new AccessTokenFileWriter(_protector, _path).Delete();
    }

    [Fact]
    public void Read_UnknownVersion_ReturnsUnknownVersion()
    {
        WriteRaw("{\"version\":2,\"access_token\":\"t\",\"expires_at\":9999999999}"u8.ToArray());

        var result = Reader().Read();

        Assert.Equal(2, Assert.IsType<AccessTokenReadResult.UnknownVersion>(result).Version);
    }

    [Fact]
    public void Read_BrokenJson_ReturnsUnreadable()
    {
        WriteRaw("not json"u8.ToArray());

        Assert.IsType<AccessTokenReadResult.Unreadable>(Reader().Read());
    }

    [Fact]
    public void Read_EmptyAccessToken_ReturnsUnreadable()
    {
        WriteRaw("{\"version\":1,\"access_token\":\"\",\"expires_at\":9999999999}"u8.ToArray());

        Assert.IsType<AccessTokenReadResult.Unreadable>(Reader().Read());
    }
}
