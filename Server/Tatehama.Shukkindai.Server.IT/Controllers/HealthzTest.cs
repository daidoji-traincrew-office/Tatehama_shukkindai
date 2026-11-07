using System.Net;
using Tatehama.Shukkindai.Server.IT.Collection;
using Tatehama.Shukkindai.Server.IT.Fixture;
using static Tatehama.Shukkindai.Server.IT.TestUtilities.IdpTestHelper;

namespace Tatehama.Shukkindai.Server.IT.Controllers;

[Collection(WebApplicationCollection.Name)]
public sealed class HealthzTest(WebApplicationFixture fixture)
{
    private readonly WebApplicationFixture _factory = fixture;

    [Fact]
    public async Task Healthz_ReflectsBotReadiness()
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
}
