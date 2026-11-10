using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Repositories.Discord;

namespace Tatehama.Shukkindai.Server.IT.Fixture;

/// <summary>テストから操作できる <see cref="IDiscordRepository"/>。</summary>
public sealed class FakeDiscordRepository : IDiscordRepository
{
    private readonly Dictionary<ulong, string> _members = new();
    private readonly Lock _gate = new();

    /// <summary>false にすると、Discord と同期できていない状態(Unavailable)になる。</summary>
    public bool Ready { get; set; } = true;

    public bool IsReady() => Ready;

    public void SetMember(ulong id, string username)
    {
        lock (_gate) _members[id] = username;
    }

    public void RemoveMember(ulong id)
    {
        lock (_gate) _members.Remove(id);
    }

    public Task<MemberLookupResult> GetMember(ulong memberId)
    {
        if (!Ready)
        {
            return Task.FromResult(MemberLookupResult.Unavailable);
        }

        lock (_gate)
        {
            return Task.FromResult(_members.TryGetValue(memberId, out var name)
                ? MemberLookupResult.Member(name)
                : MemberLookupResult.NotMember);
        }
    }
}
