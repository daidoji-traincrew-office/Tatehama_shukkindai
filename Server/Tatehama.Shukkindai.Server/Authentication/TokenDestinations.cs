using System.Security.Claims;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Tatehama.Shukkindai.Server.Authentication;

/// <summary>
/// どの claim をどのトークンに載せるかの方針を、ここ1か所に置く。
/// アクセストークンに載せるユーザー由来の claim は sub と name だけで、それ以外は宛先なし(どのトークンにも載らない)。
/// UC-02 などで claim を足すときは、<see cref="AccessTokenClaimTypes"/> に追加する(足すこと自体が仕様変更なので、
/// Docs/authentication.md と IT の許可リストも一緒に直すこと)。
/// </summary>
public static class TokenDestinations
{
    public static readonly IReadOnlySet<string> AccessTokenClaimTypes =
        new HashSet<string> { Claims.Subject, Claims.Name };

    public static void Apply(ClaimsIdentity identity) =>
        identity.SetDestinations(claim =>
            AccessTokenClaimTypes.Contains(claim.Type) ? [Destinations.AccessToken] : []);
}
