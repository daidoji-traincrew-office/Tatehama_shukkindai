using Microsoft.Extensions.Options;
using OpenIddict.Client;
using OpenIddict.Server;
using OpenIddict.Validation;
using Tatehama.Shukkindai.Server.Services.Certificate;

namespace Tatehama.Shukkindai.Server.HostedService;

/// <summary>
/// 起動時に証明書を読み込み(足りなければ生成)、24 時間ごとにローテーションと状態のログ・メトリクスの更新を行う。
/// 中身は <see cref="ICertificateService"/> にある。ここは「いつ呼ぶか」だけを持つ。
/// 最初の hosted service として登録される(Program.cs)ので、証明書の不備は Discord への接続より先に起動を止める。
/// </summary>
public sealed class CertificateRotationHostedService(
    ICertificateService certificateService,
    IOptionsMonitor<OpenIddictServerOptions> serverOptions,
    IOptionsMonitor<OpenIddictClientOptions> clientOptions,
    IOptionsMonitor<OpenIddictValidationOptions> validationOptions,
    // 注入するのは、メトリクス(ObservableGauge)を登録するため。使うのはコンストラクターだけ
    CertificateMetrics metrics) : BackgroundService
{
    private readonly CertificateMetrics _metrics = metrics;

    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>
    /// Kestrel が listen する前(hosted service の起動の段階)に、証明書をそろえて OpenIddict の options を作る。
    /// 有効な証明書が無い場合は、ここで例外になり、起動が止まる。
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // --- 1. 証明書をそろえる(事前確認 → ロック → 生成・削除・公開 → 候補の選択。足りなければここで例外=起動停止) ---
        certificateService.GetStartupCertificates();

        // --- 2. OpenIddict の options をここで作らせる ---
        // options は最初に取り出されたときに 1 回だけ作られる(動いているプロセスでは作り直さない)。
        // リクエストが来てからではなく、ここで OpenIddict の検証(有効な証明書があるか)まで済ませる
        _ = serverOptions.CurrentValue;
        _ = clientOptions.CurrentValue;
        _ = validationOptions.CurrentValue;
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 起動直後に 1 回(状態のログとメトリクスを出す)、その後は 24 時間ごとに Maintain を呼ぶ
        using var timer = new PeriodicTimer(Interval);
        do
        {
            certificateService.Maintain();
        } while (await WaitForNextTick(timer, stoppingToken));
    }

    private static async Task<bool> WaitForNextTick(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
