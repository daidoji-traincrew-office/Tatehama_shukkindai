using Microsoft.Extensions.Options;

namespace Tatehama.Shukkindai.Server.Configuration;

/// <summary>設定セクション <c>Authentication</c>。</summary>
public sealed class AuthenticationSettings
{
    public const string SectionName = "Authentication";

    /// <summary>issuer。末尾の <c>/</c> まで完全一致で使う。</summary>
    public string? Issuer { get; set; }

    /// <summary>出勤台クライアントのリダイレクト URI(<c>http://localhost:{port}/</c>)に使うポート。</summary>
    public int[] ClientRedirectPorts { get; set; } = [];

    /// <summary>証明書(pfx と .cer)を置くディレクトリ。</summary>
    public string CertificateDirectory { get; set; } = "cert";

    public Uri IssuerUri => new(Issuer!, UriKind.Absolute);
}

public sealed class AuthenticationSettingsValidator : IValidateOptions<AuthenticationSettings>
{
    public ValidateOptionsResult Validate(string? name, AuthenticationSettings options)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            errors.Add("Authentication:Issuer は必須です。");
        }
        else if (!Uri.TryCreate(options.Issuer, UriKind.Absolute, out var issuer) ||
                 issuer.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add("Authentication:Issuer はhttps の絶対 URI でなければなりません。");
        }
        else if (!options.Issuer.EndsWith('/'))
        {
            errors.Add("Authentication:Issuer は末尾が '/' でなければなりません。");
        }

        if (options.ClientRedirectPorts is null || options.ClientRedirectPorts.Length == 0)
        {
            errors.Add("Authentication:ClientRedirectPorts は1つ以上必要です。");
        }
        else if (options.ClientRedirectPorts.Any(p => p is < 1 or > 65535))
        {
            errors.Add("Authentication:ClientRedirectPorts は 1〜65535 でなければなりません。");
        }

        if (string.IsNullOrWhiteSpace(options.CertificateDirectory))
        {
            errors.Add("Authentication:CertificateDirectory は必須です。");
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
