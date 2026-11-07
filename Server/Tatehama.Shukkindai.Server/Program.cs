using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenIddict.Client;
using OpenIddict.Server;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;
using Tatehama.Shukkindai.Server.Authentication;
using Tatehama.Shukkindai.Server.Configuration;
using Tatehama.Shukkindai.Server.Data;
using Tatehama.Shukkindai.Server.HostedService;
using Tatehama.Shukkindai.Server.Repositories.Certificate;
using Tatehama.Shukkindai.Server.Repositories.Database;
using Tatehama.Shukkindai.Server.Repositories.Discord;
using Tatehama.Shukkindai.Server.Services;
using Tatehama.Shukkindai.Server.Services.Certificate;

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

        ConfigureServices(builder, authenticationSettings, discordSettings, isDevelopment);

        var app = builder.Build();
        Configure(app, isDevelopment);
        ConfigureEndpoints(app);
        await RegisterOpenIddictApplication(app);

        await app.RunAsync();
    }

    private static void ConfigureServices(
        WebApplicationBuilder builder, AuthenticationSettings authenticationSettings,
        DiscordSettings discordSettings, bool isDevelopment)
    {
        ConfigureOptions(builder);
        ConfigureLogging(builder);
        ConfigureForwardedHeadersService(builder);
        ConfigureDatabase(builder);
        ConfigureAuthenticationServices(builder);

        var openIddict = ConfigureOpenIddictServer(builder, authenticationSettings, isDevelopment);
        ConfigureOpenIddictClient(openIddict, discordSettings, isDevelopment);
        if (!isDevelopment)
        {
            ConfigureCertificateServices(builder);
        }

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            ConfigureOpenTelemetryService(builder);
        }

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddControllers();
        builder.Services.AddSingleton<DiscordRepository>();
        builder.Services.AddSingleton<IDiscordRepository>(sp => sp.GetRequiredService<DiscordRepository>());
        builder.Services.AddSingleton<IDiscordService, DiscordService>();
        builder.Services.AddHostedService<DiscordBotHostedService>();
        builder.Services.AddScoped<IUserLoginObserver, NoOpUserLoginObserver>();
        builder.Services.AddScoped<ITokenService, TokenService>();
        builder.Services.AddScoped<IClientApplicationService, ClientApplicationService>();
        builder.Services.AddScoped<IDatabaseRepository, DatabaseRepository>();
        builder.Services.AddScoped<IHealthService, HealthService>();
        builder.Services.AddScoped<ITokenPruningService, TokenPruningService>();
        builder.Services.AddHostedService<TokenPruningHostedService>();
    }

    private static void Configure(WebApplication app, bool isDevelopment)
    {
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
        app.MapGet("/healthz", async (IHealthService healthService, CancellationToken ct) =>
                await healthService.IsHealthy(ct)
                    ? Results.Ok()
                    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
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
        WebApplicationBuilder builder, AuthenticationSettings settings, bool isDevelopment)
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

                // Development 以外の証明書は、OpenIddictServerCertificateConfiguration(ConfigureCertificateServices)が登録する
                if (isDevelopment)
                {
                    options.AddDevelopmentEncryptionCertificate()
                        .AddDevelopmentSigningCertificate();
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
        OpenIddictBuilder openIddict, DiscordSettings discordSettings, bool isDevelopment)
    {
        openIddict.AddClient(options =>
        {
            options.AllowAuthorizationCodeFlow();

            if (isDevelopment)
            {
                options.AddDevelopmentEncryptionCertificate()
                    .AddDevelopmentSigningCertificate();
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

    /// <summary>
    /// 証明書の自動ローテーション(Development 以外)。証明書は cert/ の用途別ディレクトリで管理し、
    /// 起動時(Kestrel が listen する前)にそろえて OpenIddict に登録する。
    /// 流れは Docs/certificate-rotation.md の「処理の流れ」を参照。
    /// </summary>
    private static void ConfigureCertificateServices(WebApplicationBuilder builder)
    {
        // 層ごと: Repository(ファイルの I/O)→ Service(判断と順序)→ OpenIddict への登録 → HostedService(呼ぶ時期)
        builder.Services.AddSingleton<ICertificateRepository, CertificateRepository>();
        builder.Services.AddSingleton<ICertificateService, CertificateService>();
        builder.Services.AddSingleton<CertificateMetrics>();
        builder.Services.AddSingleton<IConfigureOptions<OpenIddictServerOptions>, OpenIddictServerCertificateConfiguration>();
        builder.Services.AddSingleton<IConfigureOptions<OpenIddictClientOptions>, OpenIddictClientCertificateConfiguration>();
        // 最初の hosted service にする(Discord への接続より先に、証明書の不備で止める)
        builder.Services.AddHostedService<CertificateRotationHostedService>();
    }

    /// <summary>OTEL_EXPORTER_OTLP_ENDPOINT が設定されているときだけ有効にする(Crew と同じ)。</summary>
    private static void ConfigureOpenTelemetryService(WebApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var otel = builder.Services.AddOpenTelemetry();
        otel.WithMetrics(metrics =>
        {
            metrics.AddAspNetCoreInstrumentation();
            metrics.AddHttpClientInstrumentation();
            metrics.AddMeter(CertificateMetrics.MeterName);
        });
        otel.WithTracing(tracing =>
        {
            tracing.AddAspNetCoreInstrumentation();
            tracing.AddHttpClientInstrumentation();
        });
        otel.UseOtlpExporter();
    }
}
