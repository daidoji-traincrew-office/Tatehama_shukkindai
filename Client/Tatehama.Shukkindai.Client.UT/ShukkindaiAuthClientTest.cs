using OpenIddict.Abstractions;
using Tatehama.Shukkindai.Client.Core;
using static OpenIddict.Abstractions.OpenIddictExceptions;

namespace Tatehama.Shukkindai.Client.UT;

public sealed class ShukkindaiAuthClientTest
{
    [Theory]
    [InlineData(OpenIddictConstants.Errors.AccessDenied, AuthFailureKind.AccessDenied)]
    [InlineData(OpenIddictConstants.Errors.TemporarilyUnavailable, AuthFailureKind.TemporarilyUnavailable)]
    [InlineData(OpenIddictConstants.Errors.InvalidGrant, AuthFailureKind.InvalidGrant)]
    [InlineData(OpenIddictConstants.Errors.ServerError, AuthFailureKind.Transient)]
    [InlineData(OpenIddictConstants.Errors.InvalidRequest, AuthFailureKind.Transient)]
    public void ToFailure_ProtocolException_ClassifiesByError(string error, AuthFailureKind expected)
    {
        var failure = ShukkindaiAuthClient.ToFailure(new ProtocolException("message", error));

        Assert.Equal(expected, failure.Kind);
        Assert.Equal(error, failure.Error);
    }

    [Fact]
    public void ToFailure_NetworkError_IsTransient()
    {
        var failure = ShukkindaiAuthClient.ToFailure(new HttpRequestException());

        Assert.Equal(AuthFailureKind.Transient, failure.Kind);
    }
}
