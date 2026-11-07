using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Repositories.Certificate;
using Tatehama.Shukkindai.Server.Services;
using Tatehama.Shukkindai.Server.UT.TestUtilities;

namespace Tatehama.Shukkindai.Server.UT.Services;

public sealed class CertificateMetricsTest : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _directory = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly CertificateRepository _repository;
    private readonly CertificateService _service;
    private readonly CertificateMetrics _metrics;

    public CertificateMetricsTest()
    {
        _repository = new CertificateRepository(_directory.Path);
        _service = new CertificateService(_repository, _time, new FakeLogger<CertificateService>());
        _metrics = new CertificateMetrics(_service, _time);
    }

    public void Dispose()
    {
        _metrics.Dispose();
        _directory.Dispose();
    }

    private void Seed(CertificatePurpose purpose, double notBeforeDays, double notAfterDays) =>
        _repository.Create(purpose, Start.AddDays(notBeforeDays), Start.AddDays(notAfterDays));

    private void SeedSigningWithPendingNextKey()
    {
        Seed(CertificatePurpose.ServerEncryption, -10, 700);
        Seed(CertificatePurpose.ClientSigning, -10, 700);
        Seed(CertificatePurpose.ClientEncryption, -10, 700);
        Seed(CertificatePurpose.ServerSigning, -600, 100);
        Seed(CertificatePurpose.ServerSigning, 40, 770);
    }

    /// <summary>メトリクスを 1 回読み取る(OpenTelemetry の Exporter が定期的にやること)。名前 → (purpose → 値)</summary>
    private Dictionary<string, Dictionary<string, double>> Collect()
    {
        var result = new Dictionary<string, Dictionary<string, double>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CertificateMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var purpose = "";
            foreach (var tag in tags)
            {
                if (tag.Key == CertificateMetrics.PurposeTag)
                {
                    purpose = (string)tag.Value!;
                }
            }

            if (!result.TryGetValue(instrument.Name, out var values))
            {
                result[instrument.Name] = values = new();
            }

            values[purpose] = value;
        }

        listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
        listener.SetMeasurementEventCallback<int>((i, v, t, _) => Record(i, v, t));
        listener.Start();
        listener.RecordObservableInstruments();
        return result;
    }

    [Fact]
    public void Collect_BeforeStartup_ReportsNothing()
    {
        Assert.Empty(Collect());
    }

    [Fact]
    public void Collect_PendingNextKey_ReportsRemainingTimeOfTheKeyInUsePerPurpose()
    {
        SeedSigningWithPendingNextKey();
        _service.GetStartupCertificates();

        var values = Collect();

        var remaining = values[CertificateMetrics.RemainingTimeName];
        Assert.Equal(4, remaining.Count);
        Assert.Equal(TimeSpan.FromDays(100).TotalSeconds, remaining["server-signing"]); // 使用中は old
        Assert.Equal(TimeSpan.FromDays(700).TotalSeconds, remaining["server-encryption"]);
        // 最も遠い NotAfter(=次の鍵)
        Assert.Equal(TimeSpan.FromDays(770).TotalSeconds, values[CertificateMetrics.NewestRemainingTimeName]["server-signing"]);
    }

    [Fact]
    public void Collect_PendingNextKey_ReportsNextSigningExistsAndTimeUntilActivation()
    {
        SeedSigningWithPendingNextKey();
        _service.GetStartupCertificates();

        var values = Collect();

        Assert.Equal(1, values[CertificateMetrics.NextSigningExistsName][""]);
        Assert.Equal(TimeSpan.FromDays(40).TotalSeconds, values[CertificateMetrics.NextSigningActivationTimeName][""]);
    }

    [Fact]
    public void Collect_NoPendingKey_ReportsNoNextSigningAndNoActivationTime()
    {
        _service.GetStartupCertificates();

        var values = Collect();

        Assert.Equal(0, values[CertificateMetrics.NextSigningExistsName][""]);
        Assert.False(values.ContainsKey(CertificateMetrics.NextSigningActivationTimeName));
    }

    [Fact]
    public void Collect_TimeAdvancesWithoutMaintain_RemainingTimeDecreasesAndRestartRequiredTurnsOn()
    {
        SeedSigningWithPendingNextKey();
        _service.GetStartupCertificates();
        Assert.Equal(0, Collect()[CertificateMetrics.RestartRequiredName]["server-signing"]);

        _time.Advance(TimeSpan.FromDays(41));
        var values = Collect();

        Assert.Equal(1, values[CertificateMetrics.RestartRequiredName]["server-signing"]);
        Assert.Equal(0, values[CertificateMetrics.RestartRequiredName]["server-encryption"]);
        Assert.Equal(TimeSpan.FromDays(59).TotalSeconds, values[CertificateMetrics.RemainingTimeName]["server-signing"]);
        Assert.False(values.ContainsKey(CertificateMetrics.NextSigningActivationTimeName)); // もう有効になった
        Assert.Equal(0, values[CertificateMetrics.NextSigningExistsName][""]);
    }

    [Fact]
    public void Collect_InUseKeyExpired_ReportsNegativeRemainingTime()
    {
        SeedSigningWithPendingNextKey();
        _service.GetStartupCertificates();

        _time.Advance(TimeSpan.FromDays(101));

        Assert.Equal(TimeSpan.FromDays(-1).TotalSeconds, Collect()[CertificateMetrics.RemainingTimeName]["server-signing"]);
    }

    [Fact]
    public void Collect_AfterStartupAndMaintain_ReportsLastRotationSuccessAsUnixTime()
    {
        _service.GetStartupCertificates();
        Assert.Equal(Start.ToUnixTimeSeconds(), Collect()[CertificateMetrics.LastSuccessName][""]);

        _time.Advance(TimeSpan.FromHours(24));
        _service.Maintain();

        Assert.Equal(_time.GetUtcNow().ToUnixTimeSeconds(), Collect()[CertificateMetrics.LastSuccessName][""]);
    }

    [Fact]
    public void Instruments_Always_UseOtelNamesAndUnits()
    {
        var instruments = new List<(string Name, string? Unit)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == CertificateMetrics.MeterName)
            {
                instruments.Add((instrument.Name, instrument.Unit));
            }
        };
        listener.Start(); // 既存の Meter の instrument も通知される

        Assert.Equal(
            new (string, string?)[]
            {
                ("shukkindai.certificate.remaining_time", "s"),
                ("shukkindai.certificate.newest_remaining_time", "s"),
                ("shukkindai.certificate.restart_required", "{bool}"),
                ("shukkindai.certificate.next_signing.exists", "{bool}"),
                ("shukkindai.certificate.next_signing.activation_time", "s"),
                ("shukkindai.certificate.rotation.last_success_timestamp", "s"),
            }.Order(),
            instruments.Order());
    }
}
