using Tatehama.Shukkindai.Server.Models;

namespace Tatehama.Shukkindai.Server.Repositories.Discord;

public interface IDiscordRepository
{
    /// <summary>
    /// ギルドのキャッシュを信用してよいか。Discord に接続していて、ギルドのメンバー全員を受け取り終えているときだけ true。
    /// </summary>
    bool IsReady();

    /// <summary>
    /// 現在の Discord サーバーのメンバーかどうかを、キャッシュだけで調べる。
    /// 同期できていなければ <see cref="MemberLookupStatus.Unavailable"/>(REST には問い合わせない)。
    /// </summary>
    Task<MemberLookupResult> GetMember(ulong memberId);
}
