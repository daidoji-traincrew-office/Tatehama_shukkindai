using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Tatehama.Shukkindai.Server.Services;
using Tatehama.Shukkindai.Server.IT.Collection;
using Tatehama.Shukkindai.Server.IT.Fixture;
using static Tatehama.Shukkindai.Server.IT.TestUtilities.IdpTestHelper;

namespace Tatehama.Shukkindai.Server.IT.Services;

[Collection(WebApplicationCollection.Name)]
public sealed class ClientApplicationServiceTest(WebApplicationFixture fixture)
{
    private readonly WebApplicationFixture _factory = fixture;

    [Fact]
    public async Task CreateOrUpdate_StaleDescriptorInRealDb_OverwritesIt()
    {
        using var client = _factory.CreateIdpClient();
        await client.GetAsync("healthz", Ct); // 起動(=登録)させる

        await using var scope = _factory.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = (await manager.FindByClientIdAsync(ClientId, Ct))!;

        // 古い記述子で上書きしたあとの処理は全部 try の中に入れる(途中で失敗しても、finally で最新の記述子に戻す)
        try
        {
            // 古い記述子(リダイレクト URI が1つだけ、revocation 権限なし)で上書きしておく
            var stale = new OpenIddictApplicationDescriptor();
            await manager.PopulateAsync(stale, application, Ct);
            stale.RedirectUris.Clear();
            stale.RedirectUris.Add(new Uri("http://localhost:51180/"));
            stale.Permissions.Remove(OpenIddictConstants.Permissions.Endpoints.Revocation);
            await manager.UpdateAsync(application, stale, Ct);
            var before = new OpenIddictApplicationDescriptor();
            await manager.PopulateAsync(before, (await manager.FindByClientIdAsync(ClientId, Ct))!, Ct);
            Assert.Single(before.RedirectUris);
            Assert.DoesNotContain(OpenIddictConstants.Permissions.Endpoints.Revocation, before.Permissions);

            await scope.ServiceProvider.GetRequiredService<IClientApplicationService>().CreateOrUpdate(Ct);

            var after = new OpenIddictApplicationDescriptor();
            await manager.PopulateAsync(after, (await manager.FindByClientIdAsync(ClientId, Ct))!, Ct);
            var expected = scope.ServiceProvider.GetRequiredService<IClientApplicationService>().CreateDescriptor();
            Assert.Equal(expected.RedirectUris.Select(u => u.AbsoluteUri).Order(),
                after.RedirectUris.Select(u => u.AbsoluteUri).Order());
            Assert.Equal(expected.Permissions.Order(), after.Permissions.Order());
            Assert.Equal(expected.Requirements.Order(), after.Requirements.Order());
            Assert.Equal(5, after.RedirectUris.Count);
        }
        finally
        {
            // 他のテストに影響しないよう、最新の記述子に戻す
            await scope.ServiceProvider.GetRequiredService<IClientApplicationService>().CreateOrUpdate(Ct);
        }
    }
}
