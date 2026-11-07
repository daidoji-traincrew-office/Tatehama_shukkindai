using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Client;
using OpenIddict.Server;
using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Services.Certificate;

namespace Tatehama.Shukkindai.Server.Authentication;

/// <summary>
/// 起動時に <see cref="ICertificateService"/> が用意した証明書を、OpenIddict の Server に登録する。
/// OpenIddict の options が最初に取り出されたとき(CertificateRotationHostedService の StartAsync)に、1 回だけ呼ばれる。
/// 動いているプロセスでは作り直さないので、鍵の切り替えは再起動のときだけになる。
/// 署名は、有効なものと、NotBefore が未来の次の鍵(JWKS に載るが、署名には使われない)。
/// 使う鍵は OpenIddict が選ぶ(有効期間内のうち NotAfter が最も遠いもの)。登録の順番は関係ない。
/// </summary>
public sealed class OpenIddictServerCertificateConfiguration(ICertificateService certificateService)
    : IConfigureOptions<OpenIddictServerOptions>
{
    public void Configure(OpenIddictServerOptions options)
    {
        var certificates = certificateService.GetStartupCertificates();
        foreach (var certificate in certificates.Get(CertificatePurpose.ServerSigning))
        {
            options.SigningCredentials.Add(new SigningCredentials(new X509SecurityKey(certificate),
                SecurityAlgorithms.RsaSha256));
        }

        foreach (var certificate in certificates.Get(CertificatePurpose.ServerEncryption))
        {
            options.EncryptionCredentials.Add(new EncryptingCredentials(new X509SecurityKey(certificate),
                SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
        }
    }
}

/// <summary>
/// Discord ログインの state token 用の証明書を、OpenIddict の Client に登録する。
/// Server 側と同じく、options が最初に取り出されたときに 1 回だけ呼ばれる。
/// </summary>
public sealed class OpenIddictClientCertificateConfiguration(ICertificateService certificateService)
    : IConfigureOptions<OpenIddictClientOptions>
{
    public void Configure(OpenIddictClientOptions options)
    {
        var certificates = certificateService.GetStartupCertificates();
        foreach (var certificate in certificates.Get(CertificatePurpose.ClientSigning))
        {
            options.SigningCredentials.Add(new SigningCredentials(new X509SecurityKey(certificate),
                SecurityAlgorithms.RsaSha256));
        }

        foreach (var certificate in certificates.Get(CertificatePurpose.ClientEncryption))
        {
            options.EncryptionCredentials.Add(new EncryptingCredentials(new X509SecurityKey(certificate),
                SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
        }
    }
}
