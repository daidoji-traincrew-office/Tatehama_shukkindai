namespace Tatehama.Shukkindai.Server.Discord;

public enum MemberLookupStatus
{
    Member,
    NotMember,
    Unavailable,
}

public sealed record MemberLookupResult(MemberLookupStatus Status, string? Username);

public interface IDiscordMemberLookup
{
    bool IsReady { get; }

    Task<MemberLookupResult> LookupAsync(ulong discordUserId, CancellationToken cancellationToken);
}
