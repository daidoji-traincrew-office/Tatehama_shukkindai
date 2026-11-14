namespace Tatehama.Shukkindai.Server.Models;

public enum MemberLookupStatus
{
    Member,
    NotMember,

    /// <summary>Discord と同期できておらず、判定できない。</summary>
    Unavailable,
}

/// <param name="Username">Status が <see cref="MemberLookupStatus.Member"/> のときだけ入る。</param>
public sealed record MemberLookupResult(MemberLookupStatus Status, string? Username)
{
    public static MemberLookupResult Member(string username) => new(MemberLookupStatus.Member, username);
    public static MemberLookupResult NotMember { get; } = new(MemberLookupStatus.NotMember, null);
    public static MemberLookupResult Unavailable { get; } = new(MemberLookupStatus.Unavailable, null);
}
