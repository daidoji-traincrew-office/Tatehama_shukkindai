using System.Security.Cryptography.X509Certificates;
using Tatehama.Shukkindai.Server.Models;

namespace Tatehama.Shukkindai.Server.Services.Certificate;

/// <summary>用途ごとの、証明書の寿命とローテーションの方針。</summary>
/// <param name="DirectoryName"><c>cert/</c> の下の用途別ディレクトリ名。ログの <c>CertificatePurpose</c> にも使う。</param>
/// <param name="RenewBefore">最も遠い NotAfter までの残りがこれ以下になったら、次の証明書を作る。</param>
/// <param name="DeleteAfterExpiry">NotAfter からこれだけたったら削除する(使用中のものは除く)。</param>
/// <param name="DeferredActivation">
/// true なら、次の証明書の NotBefore を未来にして、公開だけ先にする(署名。Crew と TTC が前もって知る必要があるため)。
/// false なら、すぐ使えるようにする。
/// </param>
public sealed record CertificateRule(
    string DirectoryName,
    string Subject,
    X509KeyUsageFlags KeyUsage,
    TimeSpan Lifetime,
    TimeSpan RenewBefore,
    TimeSpan DeleteAfterExpiry,
    bool DeferredActivation,
    bool Publish);

/// <param name="NotBefore">作る証明書の NotBefore(UTC)。</param>
/// <param name="ShortNotice">切り替えまでの猶予が <see cref="CertificateRotationPolicy.MinimumActivationDelay"/> に満たない(生成が遅れた)。</param>
public sealed record CertificateToCreate(DateTimeOffset NotBefore, DateTimeOffset NotAfter, bool Initial, bool ShortNotice);

/// <param name="BootstrapDenied">用途のディレクトリが空で、初回の生成が許可されていない(AllowCertificateBootstrap = false)。</param>
public sealed record RotationPlan(
    CertificateToCreate? Create, IReadOnlyList<CertificateInfo> Delete, bool BootstrapDenied = false);

/// <summary>
/// ローテーションの方針。時刻と証明書の一覧(メタデータ)から「作る/消す」を決める純粋関数だけを置く。I/O はしない。
/// <see cref="CertificateService"/> が、起動時と 24 時間ごとの両方で <see cref="Plan"/> を呼び、結果を Repository に実行させる。
/// 寿命・残り日数の閾値は <see cref="RuleFor"/> に集めてある(値は Docs/certificate-rotation.md の「用途と寿命」と同じ)。
/// </summary>
public static class CertificateRotationPolicy
{
    /// <summary>即時に使える証明書の NotBefore を、今よりこれだけ前にする(時計のずれ対策)。</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>署名: 切り替え(次の鍵の NotBefore)は、現在の鍵の期限のこれだけ前。</summary>
    public static readonly TimeSpan ActivateBeforeExpiry = TimeSpan.FromDays(60);

    /// <summary>署名: 生成が遅れたときでも、公開から切り替えまでにこれだけは空ける。</summary>
    public static readonly TimeSpan MinimumActivationDelay = TimeSpan.FromDays(14);

    /// <summary>署名: 次の鍵の NotBefore は、現在の鍵の期限のこれより後にはしない(両方が無効な隙間を作らない)。</summary>
    public static readonly TimeSpan LatestActivationBeforeExpiry = TimeSpan.FromDays(1);

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(365 * 2);

    // 閾値の見方(E = 最も遠い NotAfter):
    //   署名:        RenewBefore 150 日 = E-150 日に次の鍵を作る。切り替え(次の鍵の NotBefore)は E-60 日。古い鍵は E+7 日以降に削除。
    //   暗号化:      E-60 日に次の鍵を作る(すぐ有効)。E+14 日以降に削除(refresh token の復号のため)。
    //   Client 用:   E-60 日に次の鍵を作る(すぐ有効)。E+1 日以降に削除。
    public static CertificateRule RuleFor(CertificatePurpose purpose) => purpose switch
    {
        CertificatePurpose.ServerSigning => new("server-signing",
            "CN=Tatehama Shukkindai Server Signing Certificate", X509KeyUsageFlags.DigitalSignature,
            Lifetime, RenewBefore: TimeSpan.FromDays(150), DeleteAfterExpiry: TimeSpan.FromDays(7),
            DeferredActivation: true, Publish: true),
        CertificatePurpose.ServerEncryption => new("server-encryption",
            "CN=Tatehama Shukkindai Server Encryption Certificate", X509KeyUsageFlags.KeyEncipherment,
            Lifetime, RenewBefore: TimeSpan.FromDays(60), DeleteAfterExpiry: TimeSpan.FromDays(14),
            DeferredActivation: false, Publish: false),
        CertificatePurpose.ClientSigning => new("client-signing",
            "CN=Tatehama Shukkindai Client Signing Certificate", X509KeyUsageFlags.DigitalSignature,
            Lifetime, RenewBefore: TimeSpan.FromDays(60), DeleteAfterExpiry: TimeSpan.FromDays(1),
            DeferredActivation: false, Publish: false),
        CertificatePurpose.ClientEncryption => new("client-encryption",
            "CN=Tatehama Shukkindai Client Encryption Certificate", X509KeyUsageFlags.KeyEncipherment,
            Lifetime, RenewBefore: TimeSpan.FromDays(60), DeleteAfterExpiry: TimeSpan.FromDays(1),
            DeferredActivation: false, Publish: false),
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    public static bool IsValidAt(CertificateInfo certificate, DateTimeOffset now) =>
        certificate.NotBefore <= now && now <= certificate.NotAfter;

    /// <summary>
    /// 今、署名・暗号化に使うべき証明書。OpenIddict の並べ替えと同じ規則で、有効期間内のうち NotAfter が最も遠いもの。
    /// 無ければ null(全部期限切れ、または全部 NotBefore が未来)。
    /// </summary>
    public static CertificateInfo? SelectActive(IEnumerable<CertificateInfo> certificates, DateTimeOffset now) =>
        certificates.Where(c => IsValidAt(c, now)).OrderByDescending(c => c.NotAfter).FirstOrDefault();

    public static CertificateState StateOf(CertificateInfo certificate, CertificateInfo? active, DateTimeOffset now)
    {
        if (certificate.NotAfter < now)
        {
            return CertificateState.Expired;
        }

        if (certificate.NotBefore > now)
        {
            return CertificateState.Pending;
        }

        return active is not null && active.Thumbprint == certificate.Thumbprint
            ? CertificateState.Active
            : CertificateState.Standby;
    }

    /// <summary>NotBefore が未来の署名鍵のうち、最も早く有効になるもの。</summary>
    public static CertificateInfo? NextPending(IEnumerable<CertificateInfo> certificates, DateTimeOffset now) =>
        certificates.Where(c => c.NotBefore > now).OrderBy(c => c.NotBefore).FirstOrDefault();

    /// <summary>
    /// 今の時刻で使うべき証明書が、プロセスが使っているものと違うか(=再起動すれば切り替わる)。
    /// 使っているものが無い、または今は無効なのに有効なものがある場合も true。
    /// </summary>
    public static bool IsRestartRequired(PurposeStatus status, DateTimeOffset now)
    {
        var shouldUse = SelectActive(status.Certificates, now);
        return shouldUse is not null && shouldUse.Thumbprint != status.InUseActiveThumbprint;
    }

    /// <summary>
    /// 作る証明書と、消す証明書を決める。
    /// <paramref name="inUseThumbprints"/>(プロセスが起動時に読み込んだもの)は、再起動まで消さない。
    /// 消すのは、NotAfter + 保持期間 と、<paramref name="processStart"/> + 保持期間 の両方を過ぎたものだけ
    /// (再起動を長く怠ったあとの起動で、直前まで使っていた鍵を、すぐ消さないため)。
    /// <paramref name="allowBootstrap"/> が false で証明書が 1 つも無いときは、作らず <see cref="RotationPlan.BootstrapDenied"/> にする。
    /// </summary>
    public static RotationPlan Plan(
        CertificatePurpose purpose, IReadOnlyList<CertificateInfo> certificates,
        IReadOnlySet<string> inUseThumbprints, DateTimeOffset now, DateTimeOffset processStart,
        bool allowBootstrap = true)
    {
        var rule = RuleFor(purpose);

        // --- 1. 空のディレクトリ: 初回の生成が許可されていなければ、何もしない ---
        // cert/ のマウント漏れで、黙って新しい署名鍵を作らないため(呼び出し元は起動の事前確認でも止める)。
        if (certificates.Count == 0 && !allowBootstrap)
        {
            return new(null, [], BootstrapDenied: true);
        }

        // --- 2. 作るか(次の鍵が必要な時期か) ---
        var create = DecideCreate(rule, certificates, now);

        // --- 3. 消すか ---
        // 消すのは次の 3 つのどれにも当てはまらない鍵だけ:
        //   a. NotAfter + 保持期間 を過ぎていない(古い鍵で作られたものが残る間は持つ。例: 署名は古いアクセストークンの検証、暗号化は refresh token の復号)
        //   b. プロセス起動 + 保持期間 を過ぎていない(長く再起動しなかったあとの起動で、直前まで使った鍵をすぐ消さない)
        //   c. このプロセスが使っている(再起動まで残す)
        var survivors = certificates.Where(c => c.NotAfter > now).ToList();
        // 消した結果、期限内の証明書が 1 つも残らない状態にはしない(空になると、初回扱いで勝手に作り直してしまうため)。
        // 作る予定(create)があれば、新しい鍵が残るので消してよい
        var keepAll = create is null && survivors.Count == 0;
        var delete = certificates
            .Where(c => !keepAll &&
                        c.NotAfter + rule.DeleteAfterExpiry < now &&
                        processStart + rule.DeleteAfterExpiry < now &&
                        !inUseThumbprints.Contains(c.Thumbprint))
            .ToList();

        return new(create, delete);
    }

    private static CertificateToCreate? DecideCreate(
        CertificateRule rule, IReadOnlyList<CertificateInfo> certificates, DateTimeOffset now)
    {
        // --- 1. 初回: ディレクトリが空 ---
        if (certificates.Count == 0)
        {
            // 初回。署名も、すぐ使える証明書を作って起動する
            var notBefore = now - ClockSkew;
            return new(notBefore, notBefore + rule.Lifetime, Initial: true, ShortNotice: false);
        }

        // --- 2. まだ次の鍵を作る時期ではない ---
        // newest は NotBefore が未来の鍵も含めた最も遠い NotAfter(E)。次の鍵を作ると約 2 年に戻るので、重複して作らない。
        var newest = certificates.Max(c => c.NotAfter);
        var remaining = newest - now;
        if (remaining > rule.RenewBefore)
        {
            return null;
        }

        // --- 3. すぐ使う鍵(暗号化・Client 用): 今の時刻から有効にする ---
        if (!rule.DeferredActivation)
        {
            var notBefore = now - ClockSkew;
            return new(notBefore, notBefore + rule.Lifetime, Initial: false, ShortNotice: false);
        }

        // --- 4. 署名の次の鍵: 公開だけ先にして、有効になるのは未来にする ---
        if (remaining <= TimeSpan.Zero)
        {
            // 署名が全部期限切れ。黙って新しい鍵を作って起動することはしない(人が手順書で対応する)
            return null;
        }

        // 切り替え(次の鍵の NotBefore)は E-60 日。生成が遅れたときは、Crew と TTC が読み込む猶予として、公開から最低 14 日は空ける。
        // ただし E-1 日より後にはしない(古い鍵が切れる前に次の鍵が有効になっていないと、隙間ができるため)。
        var activation = newest - ActivateBeforeExpiry;
        var earliest = now + MinimumActivationDelay;
        var shortNotice = activation < earliest;
        var chosen = shortNotice ? earliest : activation;
        var latest = newest - LatestActivationBeforeExpiry;
        if (chosen > latest)
        {
            chosen = latest;
        }

        if (chosen < now - ClockSkew)
        {
            chosen = now - ClockSkew;
        }

        return new(chosen, chosen + rule.Lifetime, Initial: false, ShortNotice: shortNotice);
    }
}
