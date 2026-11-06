using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Tatehama.Shukkindai.Server.Authentication;
using Tatehama.Shukkindai.Server.Certificates;
using Tatehama.Shukkindai.Server.Configuration;
using Tatehama.Shukkindai.Server.Data;
using Tatehama.Shukkindai.Server.Discord;

namespace Tatehama.Shukkindai.Server;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var isDevelopment = builder.Environment.IsDevelopment();

        // issuer が不正なら、ここで例外にして起動を止める
        var authenticationSettings = builder.Configuration.GetSection(AuthenticationSettings.SectionName)
            .Get<AuthenticationSettings>() ?? new AuthenticationSettings();
        var validation = new AuthenticationSettingsValidator().Validate(null, authenticationSettings);
        if (validation.Failed)
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(AuthenticationSettings),
                validation.Failures);
        }

        ConfigureOptions(builder);
        ConfigureLogging(builder);
        ConfigureForwardedHeadersService(builder);
        ConfigureDatabase(builder);
        ConfigureAuthenticationServices(builder);

        var certificates = new List<(string, X509Certificate2)>();
        var openIddict = ConfigureOpenIddictServer(builder, authenticationSettings, isDevelopment, certificates);
        ConfigureOpenIddictClient(builder, openIddict, authenticationSettings, isDevelopment, certificates);

        builder.Services.AddControllers();
        builder.Services.AddSingleton<DiscordMemberLookup>();
        builder.Services.AddSingleton<IDiscordMemberLookup>(sp => sp.GetRequiredService<DiscordMemberLookup>());
        builder.Services.AddHostedService<DiscordBotHostedService>();
        builder.Services.AddSingleton<IUserLoginObserver, NoOpUserLoginObserver>();
        builder.Services.AddScoped<TokenPrincipalService>();
        builder.Services.AddScoped<ClientApplicationRegistrar>();

        var app = builder.Build();

        CertificateStore.WarnIfExpiring(certificates, app.Logger, DateTimeOffset.UtcNow);

        if (!isDevelopment)
        {
            app.UseForwardedHeaders();
            app.UseHsts();
        }

        app.UseHttpLogging();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/healthz", async (ApplicationDbContext db, IDiscordMemberLookup lookup, CancellationToken ct) =>
            {
                var databaseOk = false;
                try
                {
                    databaseOk = await db.Database.CanConnectAsync(ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    app.Logger.LogError(e, "Health check: database connection failed.");
                }

                return databaseOk && lookup.IsReady
                    ? Results.Ok()
                    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            })
            .AllowAnonymous()
            .WithHttpLogging(HttpLoggingFields.None);
        app.MapControllers();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ClientApplicationRegistrar>().EnsureRegisteredAsync();
        }

        await app.RunAsync();
    }

    private static void ConfigureOptions(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IValidateOptions<AuthenticationSettings>, AuthenticationSettingsValidator>();
        builder.Services.AddSingleton<IValidateOptions<DiscordSettings>, DiscordSettingsValidator>();
        builder.Services.AddOptions<AuthenticationSettings>()
            .Bind(builder.Configuration.GetSection(AuthenticationSettings.SectionName))
            .ValidateOnStart();
        builder.Services.AddOptions<DiscordSettings>()
            .Bind(builder.Configuration.GetSection(DiscordSettings.SectionName))
            .ValidateOnStart();
        builder.Services.Configure<ExternalLoginOptions>(_ => { });
    }

    private static void ConfigureLogging(WebApplicationBuilder builder)
    {
        // 1リクエスト=1レコード。ヘッダーの値は既定でマスクされる(Cookie や Authorization は許可リストに足さない)
        builder.Services.AddHttpLogging(options =>
        {
            options.LoggingFields = HttpLoggingFields.RequestPropertiesAndHeaders
                                    | HttpLoggingFields.ResponseStatusCode
                                    | HttpLoggingFields.Duration;
        });
    }

    private static void ConfigureForwardedHeadersService(WebApplicationBuilder builder)
    {
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            if (IPAddress.TryParse(builder.Configuration["ProxyIP"], out var ip))
            {
                options.KnownProxies.Add(ip);
            }
        });
    }

    private static void ConfigureDatabase(WebApplicationBuilder builder)
    {
        builder.Services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
            options.UseOpenIddict();
        });
    }

    private static void ConfigureAuthenticationServices(WebApplicationBuilder builder)
    {
        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => options.ExpireTimeSpan = TimeSpan.FromMinutes(10));
        builder.Services.AddAuthorization();
    }

    private static OpenIddictBuilder ConfigureOpenIddictServer(
        WebApplicationBuilder builder, AuthenticationSettings settings, bool isDevelopment,
        List<(string, X509Certificate2)> certificates)
    {
        return builder.Services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<ApplicationDbContext>())
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris("connect/authorize")
                    .SetTokenEndpointUris("connect/token")
                    .SetRevocationEndpointUris("connect/revoke");

                options.AllowAuthorizationCodeFlow()
                    .AllowRefreshTokenFlow()
                    .RequireProofKeyForCodeExchange();

                options.SetIssuer(settings.IssuerUri);

                // アクセストークンは JWS(暗号化しない)。aud は付けない
                options.DisableAccessTokenEncryption();
                options.SetAccessTokenLifetime(TimeSpan.FromHours(1))
                    .SetRefreshTokenLifetime(TimeSpan.FromDays(7));

                if (isDevelopment)
                {
                    options.AddDevelopmentEncryptionCertificate()
                        .AddDevelopmentSigningCertificate();
                }
                else
                {
                    var encryption = CertificateStore.LoadServerEncryptionCertificate(settings.CertificateDirectory);
                    var signing = CertificateStore.LoadServerSigningCertificate(settings.CertificateDirectory);
                    certificates.Add((CertificateStore.ServerEncryptionCertificateName, encryption));
                    certificates.Add((CertificateStore.ServerSigningCertificateName, signing));
                    options.AddEncryptionCertificate(encryption);
                    options.AddSigningCertificate(signing);
                }

                options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough();
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });
    }

    private static void ConfigureOpenIddictClient(
        WebApplicationBuilder builder, OpenIddictBuilder openIddict, AuthenticationSettings settings,
        bool isDevelopment, List<(string, X509Certificate2)> certificates)
    {
        openIddict.AddClient(options =>
        {
            options.AllowAuthorizationCodeFlow();

            if (isDevelopment)
            {
                options.AddDevelopmentEncryptionCertificate()
                    .AddDevelopmentSigningCertificate();
            }
            else
            {
                var encryption = CertificateStore.LoadClientEncryptionCertificate(settings.CertificateDirectory);
                var signing = CertificateStore.LoadClientSigningCertificate(settings.CertificateDirectory);
                certificates.Add((CertificateStore.ClientEncryptionCertificateName, encryption));
                certificates.Add((CertificateStore.ClientSigningCertificateName, signing));
                options.AddEncryptionCertificate(encryption);
                options.AddSigningCertificate(signing);
            }

            options.UseAspNetCore()
                .EnableRedirectionEndpointPassthrough();

            options.UseSystemNetHttp()
                .SetProductInformation(typeof(Program).Assembly);

            options.UseWebProviders()
                .AddDiscord(discord =>
                {
                    // ギルドの確認は Bot で行うので、identify だけでよい
                    discord.AddScopes("identify");
                    discord
                        .SetClientId(builder.Configuration["Discord:ClientId"] ?? "")
                        .SetClientSecret(builder.Configuration["Discord:ClientSecret"] ?? "")
                        .SetRedirectUri("auth/callback");
                });

            options.AddDiscordIssuerParameterSupport();
        });
    }
}
