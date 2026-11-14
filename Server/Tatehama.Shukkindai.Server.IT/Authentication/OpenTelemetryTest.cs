using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Tatehama.Shukkindai.Server.IT.Collection;
using Tatehama.Shukkindai.Server.IT.Fixture;
using static Tatehama.Shukkindai.Server.IT.TestUtilities.IdpTestHelper;

namespace Tatehama.Shukkindai.Server.IT.Authentication;

/// <summary>OpenTelemetry は、OTEL_EXPORTER_OTLP_ENDPOINT が設定されているときだけ有効になる(Crew と同じ)。</summary>
[Collection(WebApplicationCollection.Name)]
public sealed class OpenTelemetryTest(WebApplicationFixture fixture)
{
    [Fact]
    public void Startup_WithoutOtlpEndpoint_DoesNotEnableOpenTelemetry()
    {
        using var factory = fixture.WithWebHostBuilder(_ => { });

        factory.CreateIdpClient();

        Assert.Null(factory.Services.GetService<MeterProvider>());
    }

    [Fact]
    public void Startup_WithOtlpEndpoint_EnablesMeterAndTracerProviders()
    {
        // 接続先は存在しない(エクスポートは失敗するだけで、アプリには影響しない)
        using var factory = fixture.WithWebHostBuilder(builder =>
            builder.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:9"));

        factory.CreateIdpClient();

        Assert.NotNull(factory.Services.GetService<MeterProvider>());
        Assert.NotNull(factory.Services.GetService<TracerProvider>());
    }
}
