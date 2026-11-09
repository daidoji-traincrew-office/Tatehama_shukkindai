using System.Security.Cryptography.X509Certificates;
using Tatehama.Shukkindai.Server.Models;

namespace Tatehama.Shukkindai.Server.Repositories.Certificate;

/// <summary>
/// 証明書ファイルの I/O だけを担当する(いつ作る・消すかの判断は Service)。
/// レイアウトは <c>cert/{用途}/{NotBefore}-{Thumbprint 先頭8桁}.pfx</c> と、署名用の <c>cert/public/server-signing-{Thumbprint}.cer</c>。
/// </summary>
public interface ICertificateRepository
{
    /// <summary>用途ごとの pfx の一覧(<c>*.tmp</c> は無視する)。ディレクトリが無ければ空。</summary>
    IReadOnlyList<CertificateInfo> GetAll(CertificatePurpose purpose);

    /// <summary>秘密鍵つきで読み込む。呼び出し側がプロセスの間、保持する。</summary>
    X509Certificate2 Load(CertificateInfo certificate);

    /// <summary>
    /// 証明書を作って、pfx を原子的に書く(一時ファイルに書いてから move)。
    /// 署名用の .cer は <see cref="SyncPublic"/> が書く。
    /// </summary>
    CertificateInfo Create(CertificatePurpose purpose, DateTimeOffset notBefore, DateTimeOffset notAfter);

    /// <summary>pfx を削除する。</summary>
    void Delete(CertificateInfo certificate);

    /// <summary>
    /// <c>public/</c> の .cer を、<paramref name="signingCertificates"/> と一致させる。
    /// 無いものは原子的に書き、内容が違うものは書き直し、余分な .cer は消す。古い一時ファイルも消す。
    /// <paramref name="retainedPublicKeys"/>(Thumbprint → .cer の中身)は、pfx が無くなったがこのプロセスがまだ署名に使っている鍵。
    /// その .cer は消さず、無ければこの中身で書き戻す(pfx からは作り直せないため、既に正しい .cer があれば書き直さない)。
    /// 同じ Thumbprint の pfx が <paramref name="signingCertificates"/> にあれば、pfx の方を優先する。
    /// 返り値は、書いた・消した .cer のファイル名。
    /// </summary>
    IReadOnlyList<string> SyncPublic(IReadOnlyList<CertificateInfo> signingCertificates,
        IReadOnlyDictionary<string, byte[]> retainedPublicKeys);

    /// <summary>
    /// <c>.rotation.lock</c> の排他ロックの中で実行する。複数のインスタンスが同じ <c>cert/</c> を共有していても、
    /// 同時に生成しない。取れなければ待ち、上限を過ぎたら例外。
    /// </summary>
    T WithLock<T>(Func<T> action);

    /// <summary><c>public/</c> に、この署名鍵と同じ内容の .cer があるか(Crew と TTC がこの鍵を見られる状態か)。</summary>
    bool IsPublished(CertificateInfo signingCertificate);

    /// <summary><c>public/</c> にある、.cer と一時ファイル(.tmp)以外のファイル名(特に pfx は、あってはならない)。</summary>
    IReadOnlyList<string> FindUnexpectedPublicFiles();

    /// <summary>Unix で、想定より緩い権限のファイルとディレクトリ(グループ・その他に余計な権限がある)。</summary>
    IReadOnlyList<string> FindLoosePermissions();
}
