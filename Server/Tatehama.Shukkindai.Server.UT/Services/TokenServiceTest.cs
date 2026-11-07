using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using OpenIddict.Abstractions;

using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Services;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tatehama.Shukkindai.Server.UT.Services;

public class TokenServiceTest
{
    private const ulong UserId = 123456789012345678UL;

    private readonly Mock<IDiscordService> _discordService = new();
    private readonly Mock<IUserLoginObserver> _observer = new();
    private readonly TokenService _service;

    public TokenServiceTest()
    {
        _service = new TokenService(_discordService.Object, _observer.Object,
            NullLogger<TokenService>.Instance);
    }

    private void SetLookup(MemberLookupStatus status, string? username = null) =>
        _discordService.Setup(l => l.GetMemberById(UserId))
            .ReturnsAsync(new MemberLookupResult(status, username));

    private static ClaimsPrincipal CreateRefreshPrincipal(string? sub, string name, params string[] scopes)
    {
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name,
            Claims.Role);
        if (sub is not null) identity.AddClaim(new Claim(Claims.Subject, sub));
        identity.AddClaim(new Claim(Claims.Name, name));
        identity.SetScopes(scopes);
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task CreatePrincipalForAuthorization_Member_SetsSubAndNameOnlyForAccessToken()
    {
        SetLookup(MemberLookupStatus.Member, "alice");

        var result = await _service.CreatePrincipalForAuthorization(UserId, [Scopes.OfflineAccess], "127.0.0.1",
            TestContext.Current.CancellationToken);

        var success = Assert.IsType<TokenPrincipalResult.Success>(result);
        var principal = success.Principal;
        Assert.Equal(UserId.ToString(), principal.GetClaim(Claims.Subject));
        Assert.Equal("alice", principal.GetClaim(Claims.Name));
        foreach (var claim in principal.Claims.Where(c => c.Type is Claims.Subject or Claims.Name))
        {
            Assert.Equal([Destinations.AccessToken], claim.GetDestinations());
        }

        // ユーザー由来の claim は sub と name だけ
        Assert.Equal(new[] { Claims.Name, Claims.Subject },
            principal.Claims.Where(c => c.Type != Claims.Private.Scope).Select(c => c.Type).Order());
        _observer.Verify(o => o.OnUserLoggedIn(UserId, "alice", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreatePrincipalForAuthorization_Member_KeepsOnlyOfflineAccessScope()
    {
        SetLookup(MemberLookupStatus.Member, "alice");

        var result = await _service.CreatePrincipalForAuthorization(UserId,
            [Scopes.OpenId, Scopes.Profile, "OFFLINE_ACCESS", "other"], "127.0.0.1",
            TestContext.Current.CancellationToken);

        var principal = Assert.IsType<TokenPrincipalResult.Success>(result).Principal;
        // 大文字小文字違いで来ても、正規の表記(小文字)で1つだけ返る
        Assert.Equal([Scopes.OfflineAccess], principal.GetScopes());
        Assert.DoesNotContain(principal.GetScopes(), s => s is Scopes.OpenId or Scopes.Profile or "other");
    }

    [Fact]
    public async Task CreatePrincipalForAuthorization_Member_WithoutOfflineAccess_HasNoScopes()
    {
        SetLookup(MemberLookupStatus.Member, "alice");

        var result = await _service.CreatePrincipalForAuthorization(UserId, [Scopes.OpenId], "127.0.0.1",
            TestContext.Current.CancellationToken);

        Assert.Empty(Assert.IsType<TokenPrincipalResult.Success>(result).Principal.GetScopes());
    }

    [Fact]
    public async Task CreatePrincipalForAuthorization_NotMember_IsAccessDenied_AndObserverIsNotCalled()
    {
        SetLookup(MemberLookupStatus.NotMember);

        var result = await _service.CreatePrincipalForAuthorization(UserId, [Scopes.OfflineAccess], "127.0.0.1",
            TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<TokenPrincipalResult.Rejected>(result);
        Assert.Equal(Errors.AccessDenied, rejected.Error);
        Assert.Equal("Discord サーバーのメンバーではありません。", rejected.Description);
        _observer.Verify(o => o.OnUserLoggedIn(It.IsAny<ulong>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreatePrincipalForAuthorization_Unavailable_IsTemporarilyUnavailable()
    {
        SetLookup(MemberLookupStatus.Unavailable);

        var result = await _service.CreatePrincipalForAuthorization(UserId, [Scopes.OfflineAccess], "127.0.0.1",
            TestContext.Current.CancellationToken);

        Assert.Equal(Errors.TemporarilyUnavailable, Assert.IsType<TokenPrincipalResult.Rejected>(result).Error);
        _observer.Verify(o => o.OnUserLoggedIn(It.IsAny<ulong>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreatePrincipalForRefresh_NotMember_IsInvalidGrant()
    {
        SetLookup(MemberLookupStatus.NotMember);

        var result = await _service.CreatePrincipalForRefresh(
            CreateRefreshPrincipal(UserId.ToString(), "old", Scopes.OfflineAccess), TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<TokenPrincipalResult.Rejected>(result);
        Assert.Equal(Errors.InvalidGrant, rejected.Error);
        Assert.Equal("Discord サーバーのメンバーではなくなりました。", rejected.Description);
    }

    [Fact]
    public async Task CreatePrincipalForRefresh_Member_ReplacesNameWithLatest_KeepsSubAndScopes()
    {
        SetLookup(MemberLookupStatus.Member, "new-name");

        var result = await _service.CreatePrincipalForRefresh(
            CreateRefreshPrincipal(UserId.ToString(), "old-name", Scopes.OfflineAccess), TestContext.Current.CancellationToken);

        var principal = Assert.IsType<TokenPrincipalResult.Success>(result).Principal;
        Assert.Equal(UserId.ToString(), principal.GetClaim(Claims.Subject));
        Assert.Equal("new-name", principal.GetClaim(Claims.Name));
        // 古い name が残らない
        Assert.Single(principal.Claims, c => c.Type == Claims.Name);
        Assert.Equal([Scopes.OfflineAccess], principal.GetScopes());
        foreach (var claim in principal.Claims.Where(c => c.Type is Claims.Subject or Claims.Name))
        {
            Assert.Equal([Destinations.AccessToken], claim.GetDestinations());
        }

        _observer.Verify(o => o.OnUserLoggedIn(It.IsAny<ulong>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    public async Task CreatePrincipalForRefresh_InvalidSub_IsInvalidGrant(string? sub)
    {
        var result = await _service.CreatePrincipalForRefresh(CreateRefreshPrincipal(sub, "x"), TestContext.Current.CancellationToken);

        Assert.Equal(Errors.InvalidGrant, Assert.IsType<TokenPrincipalResult.Rejected>(result).Error);
        _discordService.Verify(l => l.GetMemberById(It.IsAny<ulong>()), Times.Never);
    }

    [Fact]
    public async Task CreatePrincipalForRefresh_Unavailable_IsTemporarilyUnavailable()
    {
        SetLookup(MemberLookupStatus.Unavailable);

        var result = await _service.CreatePrincipalForRefresh(
            CreateRefreshPrincipal(UserId.ToString(), "old"), TestContext.Current.CancellationToken);

        Assert.Equal(Errors.TemporarilyUnavailable, Assert.IsType<TokenPrincipalResult.Rejected>(result).Error);
    }
}
