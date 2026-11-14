using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Repositories.Discord;

namespace Tatehama.Shukkindai.Server.Services;

public interface IDiscordService
{
    bool IsReady();
    Task<MemberLookupResult> GetMemberById(ulong memberId);
}

/// <summary>
/// Discord の判定を集める層。判定そのもの(キャッシュを信用してよいか、メンバーか)は Repository が結果型で返すので、
/// 今は素通しになっている。入鋏ロールなど、Discord に関する判断を足すときはここに置く。
/// </summary>
public sealed class DiscordService(IDiscordRepository discordRepository) : IDiscordService
{
    public bool IsReady() => discordRepository.IsReady();

    public Task<MemberLookupResult> GetMemberById(ulong memberId) => discordRepository.GetMember(memberId);
}
