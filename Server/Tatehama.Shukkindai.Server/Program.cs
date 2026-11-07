using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;
using Tatehama.Shukkindai.Server.Authentication;
using Tatehama.Shukkindai.Server.Certificates;
using Tatehama.Shukkindai.Server.Configuration;
using Tatehama.Shukkindai.Server.Data;
using Tatehama.Shukkindai.Server.Discord;
using Tatehama.Shukkindai.Server.HostedService;
using Tatehama.Shukkindai.Server.Services;

namespace Tatehama.Shukkindai.Server;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var isDevelopment = builder.Environment.IsDevelopment();

        // 設定が不正なら、ここで例外にして起動を止める
        var authenticationSettings = BindAndValidate<AuthenticationSettings>(
            builder, AuthenticationSettings.SectionName, new AuthenticationSettingsValidator());
        var discordSettings = BindAndValidate<DiscordSettings>(
            builder, DiscordSettings.SectionName, new DiscordSettingsValidator());

        var certificates = new List<(string, X509Certificate2)>();
        ConfigureServices(builder, authenticationSettings, discordSettings, isDevelopment, certificates);

        var app = builder.Build();
        Configure(app, authenticationSettings, isDevelopment, certificates);
        ConfigureEndpoints(app);
        await RegisterOpenIddictApplication(app);

        await app.RunAsync();
    }

    private static void ConfigureServices(
        WebApplicationBuilder builder, AuthenticationSettings authenticationSettings,
        DiscordSettings discordSettings, bool isDevelopment, List<(string, X509Certificate2)> certificates)
    {
        ConfigureOptions(builder);
        ConfigureLogging(builder);
        ConfigureForwardedHeadersService(builder);
        ConfigureDatabase(builder);
        ConfigureAuthenticationServices(builder);

        var openIddict = ConfigureOpenIddictServer(builder, authenticationSettings, isDevelopment, certificates);
        ConfigureOpenIddictClient(openIddict, authenticationSettings, discordSettings, isDevelopment, certificates);

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddControllers();
        builder.Services.AddSingleton<DiscordMemberLookup>();
        builder.Services.AddSingleton<IDiscordMemberLookup>(sp => sp.GetRequiredService<DiscordMemberLookup>());
        builder.Services.AddHostedService<DiscordBotHostedService>();
        builder.Services.AddScoped<IUserLoginObserver, NoOpUserLoginObserver>();
        builder.Services.AddScoped<TokenPrincipalService>();
        builder.Services.AddScoped<IClientApplicationService, ClientApplicationService>();
        builder.Services.AddScoped<ITokenPruningService, TokenPruningService>();
        builder.Services.AddHostedService<TokenPruningHostedService>();
    }

    private static void Configure(
        WebApplication app, AuthenticationSettings authenticationSettings, bool isDevelopment,
        List<(string, X509Certificate2)> certificates)
    {
        CertificateStore.WarnIfExpiring(certificates, app.Logger, DateTimeOffset.UtcNow);
        foreach (var path in CertificateStore.PfxPaths(authenticationSettings.CertificateDirectory)
                     .Where(CertificateStore.IsPermissionTooOpen))
        {
            app.Logger.LogWarning("Certificate file {Path} is readable by group/others. Use mode 0600.", path);
        }

        if (!isDevelopment)
        {
            app.UseForwardedHeaders();
            app.UseHsts();
        }

        app.UseRouting();
        // WithHttpLogging(エンドポイントごとの設定)は、エンドポイントが決まった後(UseRouting の後)でないと効かない
        app.UseHttpLogging();
        app.UseAuthentication();
        app.UseAuthorization();
    }

    private static void ConfigureEndpoints(WebApplication app)
    {
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
    }

    private static async Task RegisterOpenIddictApplication(WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IClientApplicationService>().CreateOrUpdate();
    }

    private static T BindAndValidate<T>(WebApplicationBuilder builder, string section, IValidateOptions<T> validator)
        where T : class, new()
    {
        var settings = builder.Configuration.GetSection(section).Get<T>() ?? new T();
        var result = validator.Validate(null, settings);
        if (result.Failed)
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(T), result.Failures);
        }

        return settings;
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
        // IConfiguration には bind しない(本番で challenge scheme を差し替えられないようにする)
        builder.Services.AddOptions<ExternalLoginOptions>();
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
            .AddCookie(options =>
            {
                // Discord ログイン途中の Cookie。短命で、Secure・HttpOnly・__Host- プレフィックス付き
                options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
                options.Cookie.Name = "__Host-Shukkindai.Login";
                options.Cookie.Path = "/";
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
            });
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

                // openid は使わない(identity token を発行しない)。discovery にも載せず、受け付けない。
                // PKCE は S256 だけ(plain は拒否)。
                options.Configure(o =>
                {
                    o.Scopes.Remove(Scopes.OpenId);
                    o.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain);
                });

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
        OpenIddictBuilder openIddict, AuthenticationSettings settings, DiscordSettings discordSettings,
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

            // EnableErrorPassthrough が無いと、Discord がエラー(access_denied など)付きで callback に戻ってきたとき、
            // OpenIddict が自分でエラーを表示して終わり、AuthorizationController.DiscordCallback まで届かない
            // (リダイレクトのリクエスト検証の段階で state とエラーを処理するため)。
            options.UseAspNetCore()
                .EnableRedirectionEndpointPassthrough()
                .EnableErrorPassthrough();

            options.UseSystemNetHttp()
                .SetProductInformation(typeof(Program).Assembly);

            options.UseWebProviders()
                .AddDiscord(discord =>
                {
                    // ギルドの確認は Bot で行うので、要求する scope は identify だけでよい。
                    // (OpenIddict 7.8 の WebIntegration が openid も足すため、実際には "openid identify" を要求する)
                    discord.AddScopes("identify");
                    discord
                        .SetClientId(discordSettings.ClientId!)
                        .SetClientSecret(discordSettings.ClientSecret!)
                        .SetRedirectUri("auth/callback");
                });

        });
    }
}
