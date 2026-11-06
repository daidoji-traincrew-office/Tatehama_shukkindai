using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using OpenIddict.Abstractions;
using Tatehama.Shukkindai.Server.Authentication;
using Tatehama.Shukkindai.Server.Discord;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tatehama.Shukkindai.Server.UT.Authentication;

public class TokenPrincipalServiceTests
{
    private const ulong UserId = 123456789012345678UL;

    private readonly Mock<IDiscordMemberLookup> _lookup = new();
    private readonly Mock<IUserLoginObserver> _observer = new();
    private readonly TokenPrincipalService _service;

    public TokenPrincipalServiceTests()
    {
        _service = new TokenPrincipalService(_lookup.Object, _observer.Object,
            NullLogger<TokenPrincipalService>.Instance);
    }

    private void SetLookup(MemberLookupStatus status, string? username = null) =>
        _lookup.Setup(l => l.LookupAsync(UserId, It.IsAny<CancellationToken>()))
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
    public async Task Authorize_Member_SetsSubAndNameOnlyForAccessToken()
    {
        SetLookup(MemberLookupStatus.Member, "alice");

        var result = await _service.CreateForAuthorizationAsync(UserId, [Scopes.OfflineAccess], "127.0.0.1",
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
        _observer.Verify(o => o.OnUserLoggedInAsync(UserId, "alice", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Authorize_Member_KeepsOnlyOfflineAccessScope()
    {
        SetLookup(MemberLookupStatus.Member, "alice");

        var result = await _service.CreateForAuthorizationAsync(UserId,
            [Scopes.OpenId, Scopes.Profile, "OFFLINE_ACCESS", Scopes.OfflineAccess, "other"], "127.0.0.1",
            TestContext.Current.CancellationToken);

        var principal = Assert.IsType<TokenPrincipalResult.Success>(result).Principal;
        Assert.Equal([Scopes.OfflineAccess], principal.GetScopes().Select(s => s.ToLowerInvariant()).Distinct());
        Assert.DoesNotContain(principal.GetScopes(), s => s is Scopes.OpenId or Scopes.Profile or "other");
    }

    [Fact]
    public async Task Authorize_Member_WithoutOfflineAccess_HasNoScopes()
    {
        SetLookup(MemberLookupStatus.Member, "alice");

        var result = await _service.CreateForAuthorizationAsync(UserId, [Scopes.OpenId], "127.0.0.1",
            TestContext.Current.CancellationToken);

        Assert.Empty(Assert.IsType<TokenPrincipalResult.Success>(result).Principal.GetScopes());
    }

    [Fact]
    public async Task Authorize_NotMember_IsAccessDenied_AndObserverIsNotCalled()
    {
        SetLookup(MemberLookupStatus.NotMember);

        var result = await _service.CreateForAuthorizationAsync(UserId, [Scopes.OfflineAccess], "127.0.0.1",
            TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<TokenPrincipalResult.Rejected>(result);
        Assert.Equal(Errors.AccessDenied, rejected.Error);
        Assert.Equal("Discord サーバーのメンバーではありません。", rejected.Description);
        _observer.Verify(o => o.OnUserLoggedInAsync(It.IsAny<ulong>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Authorize_Unavailable_IsTemporarilyUnavailable()
    {
        SetLookup(MemberLookupStatus.Unavailable);

        var result = await _service.CreateForAuthorizationAsync(UserId, [Scopes.OfflineAccess], "127.0.0.1",
            TestContext.Current.CancellationToken);

        Assert.Equal(Errors.TemporarilyUnavailable, Assert.IsType<TokenPrincipalResult.Rejected>(result).Error);
        _observer.Verify(o => o.OnUserLoggedInAsync(It.IsAny<ulong>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_NotMember_IsInvalidGrant()
    {
        SetLookup(MemberLookupStatus.NotMember);

        var result = await _service.CreateForRefreshAsync(
            CreateRefreshPrincipal(UserId.ToString(), "old", Scopes.OfflineAccess), TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<TokenPrincipalResult.Rejected>(result);
        Assert.Equal(Errors.InvalidGrant, rejected.Error);
        Assert.Equal("Discord サーバーのメンバーではなくなりました。", rejected.Description);
    }

    [Fact]
    public async Task Refresh_Member_ReplacesNameWithLatest_KeepsSubAndScopes()
    {
        SetLookup(MemberLookupStatus.Member, "new-name");

        var result = await _service.CreateForRefreshAsync(
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

        _observer.Verify(o => o.OnUserLoggedInAsync(It.IsAny<ulong>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    public async Task Refresh_InvalidSub_IsInvalidGrant(string? sub)
    {
        var result = await _service.CreateForRefreshAsync(CreateRefreshPrincipal(sub, "x"), TestContext.Current.CancellationToken);

        Assert.Equal(Errors.InvalidGrant, Assert.IsType<TokenPrincipalResult.Rejected>(result).Error);
        _lookup.Verify(l => l.LookupAsync(It.IsAny<ulong>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_Unavailable_IsTemporarilyUnavailable()
    {
        SetLookup(MemberLookupStatus.Unavailable);

        var result = await _service.CreateForRefreshAsync(
            CreateRefreshPrincipal(UserId.ToString(), "old"), TestContext.Current.CancellationToken);

        Assert.Equal(Errors.TemporarilyUnavailable, Assert.IsType<TokenPrincipalResult.Rejected>(result).Error);
    }
}
