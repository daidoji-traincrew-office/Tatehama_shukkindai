namespace Tatehama.Shukkindai.Client.Core;

public sealed class ShukkindaiClientOptions
{
    public const string SectionName = "Shukkindai";

    public const string ClientId = "Tatehama_Shukkindai_Client";

    /// <summary>リダイレクト URI(http://localhost:{port}/)のポート。サーバーは 51180〜51184 を受け付ける。</summary>
    public int RedirectPort { get; set; } = 51180;

    /// <summary>アクセストークンの期限のこれだけ前に更新する。</summary>
    public TimeSpan RefreshBeforeExpiry { get; set; } = TimeSpan.FromMinutes(10);
}
