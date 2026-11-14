using Tatehama.Shukkindai.Client.Core;

namespace Tatehama.Shukkindai.Client.UT;

public sealed class AccessTokenClaimsTest
{
    [Fact]
    public void Parse_ValidToken_ReturnsSubNameAndExp()
    {
        var exp = DateTimeOffset.FromUnixTimeSeconds(1793956743);

        var claims = AccessTokenClaims.Parse(TestJwt.Create("900000000000740692", "alice", exp));

        Assert.Equal(new("900000000000740692", "alice", exp), claims);
    }

    [Fact]
    public void Parse_NoName_NameIsNull()
    {
        var claims = AccessTokenClaims.Parse(TestJwt.Create("1", null, DateTimeOffset.UnixEpoch));

        Assert.NotNull(claims);
        Assert.Null(claims.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a.b.c")]
    [InlineData("a.!!!.c")]
    public void Parse_Malformed_ReturnsNull(string token)
    {
        Assert.Null(AccessTokenClaims.Parse(token));
    }

    [Fact]
    public void Parse_MissingSubOrExp_ReturnsNull()
    {
        var noExp = $"h.{TestJwt.Encode("{\"sub\":\"1\"}")}.s";
        var noSub = $"h.{TestJwt.Encode("{\"exp\":1}")}.s";

        Assert.Null(AccessTokenClaims.Parse(noExp));
        Assert.Null(AccessTokenClaims.Parse(noSub));
    }
}
