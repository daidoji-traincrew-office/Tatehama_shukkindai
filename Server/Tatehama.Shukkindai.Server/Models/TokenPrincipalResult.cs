using System.Security.Claims;

namespace Tatehama.Shukkindai.Server.Models;

public abstract record TokenPrincipalResult
{
    public sealed record Success(ClaimsPrincipal Principal) : TokenPrincipalResult;

    /// <param name="Error">access_denied / invalid_grant / temporarily_unavailable など</param>
    public sealed record Rejected(string Error, string Description) : TokenPrincipalResult;
}
