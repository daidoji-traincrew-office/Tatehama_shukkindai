using System.Security.Cryptography;
using System.Text.Json;

namespace Tatehama.Shukkindai.TokenFile;

public abstract record AccessTokenReadResult
{
    public sealed record Valid(string AccessToken, DateTimeOffset ExpiresAt) : AccessTokenReadResult;

    public sealed record Expired(DateTimeOffset ExpiresAt) : AccessTokenReadResult;

    public sealed record Missing : AccessTokenReadResult;

    public sealed record Unreadable : AccessTokenReadResult;

    public sealed record UnknownVersion(int Version) : AccessTokenReadResult;
}

/// <summary>
/// 他のアプリ(ATS など)がアクセストークンファイルを読むために使う。
/// </summary>
public sealed class AccessTokenFileReader(ITokenProtector protector, string path, TimeProvider timeProvider)
{
    public AccessTokenReadResult Read()
    {
        byte[] protectedBytes;
        try
        {
            protectedBytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AccessTokenReadResult.Missing();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new AccessTokenReadResult.Unreadable();
        }

        AccessTokenFile? file;
        try
        {
            file = AccessTokenFile.FromJsonBytes(protector.Unprotect(protectedBytes));
        }
        catch (Exception e) when (e is JsonException or CryptographicException or ArgumentException
                                      or NotSupportedException)
        {
            return new AccessTokenReadResult.Unreadable();
        }

        if (file is null)
        {
            return new AccessTokenReadResult.Unreadable();
        }

        if (file.Version != AccessTokenFile.CurrentVersion)
        {
            return new AccessTokenReadResult.UnknownVersion(file.Version);
        }

        if (string.IsNullOrEmpty(file.AccessToken))
        {
            return new AccessTokenReadResult.Unreadable();
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(file.ExpiresAt);
        return expiresAt <= timeProvider.GetUtcNow()
            ? new AccessTokenReadResult.Expired(expiresAt)
            : new AccessTokenReadResult.Valid(file.AccessToken, expiresAt);
    }
}
