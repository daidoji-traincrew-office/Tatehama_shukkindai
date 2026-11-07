using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tatehama.Shukkindai.Server.IT.Collection;
using Tatehama.Shukkindai.Server.IT.Fixture;
using static Tatehama.Shukkindai.Server.IT.TestUtilities.IdpTestHelper;

namespace Tatehama.Shukkindai.Server.IT.Controllers;

[Collection(WebApplicationCollection.Name)]
public sealed class HealthzTest(WebApplicationFixture fixture)
{
    private readonly WebApplicationFixture _factory = fixture;

    [Fact]
    public async Task Healthz_DiscordNotSynchronized_Returns503()
    {
        using var client = _factory.CreateIdpClient();

        var ok = await client.GetAsync("healthz", Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        _factory.DiscordRepository.Ready = false;
        try
        {
            var notReady = await client.GetAsync("healthz", Ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        }
        finally
        {
            _factory.DiscordRepository.Ready = true;
        }
    }

    [Fact]
    public async Task Healthz_Request_IsNotWrittenToHttpLog()
    {
        // UseHttpLogging を UseRouting の前に置くと、エンドポイントごとの WithHttpLogging(None) が効かず、
        // 監視の /healthz が毎回ログに出てしまう。順序の修正を固定する
        var provider = new CapturingLoggerProvider();
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(provider)));
        using var client = factory.CreateIdpClient();

        await client.GetAsync("healthz", Ct);
        await client.GetAsync(".well-known/openid-configuration", Ct);

        var http = provider.Messages("Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware");
        Assert.Contains(http, m => m.Contains("openid-configuration"));
        Assert.DoesNotContain(http, m => m.Contains("healthz"));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(string Category, string Message)> _entries = new();

        public IReadOnlyList<string> Messages(string category) =>
            _entries.Where(e => e.Category == category).Select(e => e.Message).ToList();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category,
            System.Collections.Concurrent.ConcurrentQueue<(string, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                // HttpLogging は、リクエストの内容を state(キー/値の一覧)に入れて出す。全部つなげて調べる
                var text = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"))
                    : formatter(state, exception);
                entries.Enqueue((category, text));
            }
        }
    }
}
