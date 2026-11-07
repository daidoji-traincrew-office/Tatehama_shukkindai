using System.Diagnostics.Metrics;
using Tatehama.Shukkindai.Server.Models;

namespace Tatehama.Shukkindai.Server.Services.Certificate;

/// <summary>
/// 証明書のメトリクス(OpenTelemetry の自前の Meter)。すべて ObservableGauge で、読み取られた時点の時刻で計算する
/// (1 日 1 回の更新を待たずに、残り時間が連続して減っていく)。tag は用途だけ(カーディナリティは 4)。
/// Prometheus 互換に変換されると、名前の '.' は '_' に、単位 "s" は "_seconds" の接尾辞になる。
/// </summary>
public sealed class CertificateMetrics : IDisposable
{
    public const string MeterName = "Tatehama.Shukkindai.Server.Certificates";
    public const string PurposeTag = "certificate.purpose";

    public const string RemainingTimeName = "shukkindai.certificate.remaining_time";
    public const string NewestRemainingTimeName = "shukkindai.certificate.newest_remaining_time";
    public const string RestartRequiredName = "shukkindai.certificate.restart_required";
    public const string NextSigningExistsName = "shukkindai.certificate.next_signing.exists";
    public const string NextSigningActivationTimeName = "shukkindai.certificate.next_signing.activation_time";
    public const string LastSuccessName = "shukkindai.certificate.rotation.last_success_timestamp";

    private readonly Meter _meter = new(MeterName);
    private readonly ICertificateService _certificateService;
    private readonly TimeProvider _timeProvider;

    public CertificateMetrics(ICertificateService certificateService, TimeProvider timeProvider)
    {
        _certificateService = certificateService;
        _timeProvider = timeProvider;

        _meter.CreateObservableGauge(RemainingTimeName, ObserveRemainingTime, unit: "s",
            description: "Time until the certificate this process is using expires. Negative if already expired.");
        _meter.CreateObservableGauge(NewestRemainingTimeName, ObserveNewestRemainingTime, unit: "s",
            description: "Time until the newest certificate on disk expires. A low value means automatic rotation has stalled.");
        _meter.CreateObservableGauge(RestartRequiredName, ObserveRestartRequired, unit: "{bool}",
            description: "1 if the certificate that should be used now differs from the one this process is using (restart to switch).");
        _meter.CreateObservableGauge(NextSigningExistsName, ObserveNextSigningExists, unit: "{bool}",
            description: "1 if a server signing certificate that is not yet valid (next key) exists.");
        _meter.CreateObservableGauge(NextSigningActivationTimeName, ObserveNextSigningActivationTime, unit: "s",
            description: "Time until the next server signing certificate becomes valid. Only reported while one exists.");
        _meter.CreateObservableGauge(LastSuccessName, ObserveLastSuccess, unit: "s",
            description: "Unix time of the last certificate rotation in which every purpose succeeded.");
    }

    private static KeyValuePair<string, object?> Tag(CertificatePurpose purpose) =>
        new(PurposeTag, CertificateLog.Name(purpose));

    private IEnumerable<Measurement<double>> ObserveRemainingTime()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var purpose in _certificateService.GetStatus().Purposes)
        {
            var inUse = purpose.InUseActive ?? CertificateRotationPolicy.SelectActive(purpose.Certificates, now);
            if (inUse is not null)
            {
                yield return new Measurement<double>((inUse.NotAfter - now).TotalSeconds, Tag(purpose.Purpose));
            }
        }
    }

    private IEnumerable<Measurement<double>> ObserveNewestRemainingTime()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var purpose in _certificateService.GetStatus().Purposes)
        {
            var newest = purpose.Certificates.MaxBy(c => c.NotAfter);
            if (newest is not null)
            {
                yield return new Measurement<double>((newest.NotAfter - now).TotalSeconds, Tag(purpose.Purpose));
            }
        }
    }

    private IEnumerable<Measurement<int>> ObserveRestartRequired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var purpose in _certificateService.GetStatus().Purposes)
        {
            yield return new Measurement<int>(CertificateRotationPolicy.IsRestartRequired(purpose, now) ? 1 : 0,
                Tag(purpose.Purpose));
        }
    }

    private IEnumerable<Measurement<int>> ObserveNextSigningExists()
    {
        var now = _timeProvider.GetUtcNow();
        var signing = SigningStatus();
        if (signing is not null)
        {
            yield return new Measurement<int>(
                CertificateRotationPolicy.NextPending(signing.Certificates, now) is null ? 0 : 1);
        }
    }

    private IEnumerable<Measurement<double>> ObserveNextSigningActivationTime()
    {
        var now = _timeProvider.GetUtcNow();
        var next = SigningStatus() is { } signing ? CertificateRotationPolicy.NextPending(signing.Certificates, now) : null;
        if (next is not null)
        {
            yield return new Measurement<double>((next.NotBefore - now).TotalSeconds);
        }
    }

    private IEnumerable<Measurement<double>> ObserveLastSuccess()
    {
        if (_certificateService.GetStatus().LastRotationSuccess is { } last)
        {
            yield return new Measurement<double>(last.ToUnixTimeSeconds());
        }
    }

    private PurposeStatus? SigningStatus() =>
        _certificateService.GetStatus().Purposes.FirstOrDefault(p => p.Purpose == CertificatePurpose.ServerSigning);

    public void Dispose() => _meter.Dispose();
}
