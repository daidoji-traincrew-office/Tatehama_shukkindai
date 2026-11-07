using Tatehama.Shukkindai.Server.Discord;

namespace Tatehama.Shukkindai.Server.IT.Fixture;

/// <summary>テストから操作できる <see cref="IDiscordMemberLookup"/>。</summary>
public sealed class FakeDiscordMemberLookup : IDiscordMemberLookup
{
    private readonly Dictionary<ulong, string> _members = new();
    private readonly object _gate = new();

    public bool IsReady { get; set; } = true;

    public void SetMember(ulong id, string username)
    {
        lock (_gate) _members[id] = username;
    }

    public void RemoveMember(ulong id)
    {
        lock (_gate) _members.Remove(id);
    }

    public Task<MemberLookupResult> LookupAsync(ulong discordUserId, CancellationToken cancellationToken)
    {
        if (!IsReady)
        {
            return Task.FromResult(new MemberLookupResult(MemberLookupStatus.Unavailable, null));
        }

        lock (_gate)
        {
            return Task.FromResult(_members.TryGetValue(discordUserId, out var name)
                ? new MemberLookupResult(MemberLookupStatus.Member, name)
                : new MemberLookupResult(MemberLookupStatus.NotMember, null));
        }
    }
}
