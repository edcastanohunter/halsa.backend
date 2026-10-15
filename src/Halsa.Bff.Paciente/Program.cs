using Halsa.Bff.Paciente;
using Halsa.Bff.Paciente.Endpoints;
using Halsa.Bff.Paciente.Proxy;
using Halsa.Bff.Paciente.Seguridad;
using Halsa.Bff.Paciente.Sesion;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Serilog;
using StackExchange.Redis;

// SPEC 12: the BFF of the patient portal. The browser holds only a __Host- cookie with the session key; the tokens live
// in Redis and are used here, against Keycloak (halsa-pacientes) and the API (/v1/paciente/* only).

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => configuration.ReadFrom.Configuration(context.Configuration));

builder.Services
    .AddOptions<BffOptions>()
    .Bind(builder.Configuration.GetSection(BffOptions.SectionName))
    .Validate(o => Uri.IsWellFormedUriString(o.ApiBaseUrl, UriKind.Absolute), "Bff:ApiBaseUrl debe ser una URL absoluta.")
    .Validate(o => Uri.IsWellFormedUriString(o.Authority, UriKind.Absolute), "Bff:Authority debe ser una URL absoluta.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.ClientSecret), "Bff:ClientSecret es obligatorio (user secrets o entorno).")
    .ValidateOnStart();

BffOptions bff = builder.Configuration.GetSection(BffOptions.SectionName).Get<BffOptions>() ?? new BffOptions();

// ── Session store: Redis, with the Data Protection keys there too, so every replica reads every session ─────────────
string? redis = builder.Configuration.GetConnectionString("Redis");
IDataProtectionBuilder dataProtection = builder.Services.AddDataProtection().SetApplicationName("halsa-bff-paciente");

if (!string.IsNullOrWhiteSpace(redis))
{
    ConfigurationOptions redisOptions = ConfigurationOptions.Parse(redis);
    redisOptions.AbortOnConnectFail = false;
    IConnectionMultiplexer multiplexer = ConnectionMultiplexer.Connect(redisOptions);

    builder.Services.AddSingleton(multiplexer);
    dataProtection.PersistKeysToStackExchangeRedis(multiplexer, "halsa-bff-paciente:data-protection");
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.ConnectionMultiplexerFactory = () => Task.FromResult(multiplexer);
        options.InstanceName = "halsa-bff-paciente:";
    });
}
else if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
{
    // One process only: the sessions die with it.
    builder.Services.AddDistributedMemoryCache();
}
else
{
    throw new InvalidOperationException("ConnectionStrings:Redis es obligatorio fuera de Development y Testing.");
}

builder.Services.AddSingleton<ITicketStore, RedisTicketStore>();
builder.Services.AddSingleton<IPostConfigureOptions<CookieAuthenticationOptions>, ConfigurarSesion>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<SesionTokens>();

builder.Services.AddHttpClient(SesionTokens.ClienteKeycloak);
builder.Services.AddHttpClient(Bff.ClienteApi, client => client.BaseAddress = new Uri(bff.ApiBaseUrl.TrimEnd('/') + "/"));

// ── Authentication: the session cookie, and OIDC code + PKCE as the confidential client halsa-bff-paciente ──────────
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = Bff.CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = bff.Inactividad;
        options.SlidingExpiration = true;

        options.Events.OnSigningIn = context =>
        {
            context.HttpContext.RequestServices.GetRequiredService<SesionTokens>().AlIniciar(context);
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = context =>
            context.HttpContext.RequestServices.GetRequiredService<SesionTokens>().ValidarAsync(context);

        // An API never redirects: without a session it answers 401, without rights 403.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddOpenIdConnect(options =>
    {
        options.Authority = bff.Authority;
        options.ClientId = bff.ClientId;
        options.ClientSecret = bff.ClientSecret;
        options.RequireHttpsMetadata = bff.RequireHttpsMetadata;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;
        options.CallbackPath = Bff.CallbackPath;
        options.SignedOutCallbackPath = $"{Bff.BffPrefix}/signout-callback-oidc";
        options.TokenValidationParameters.NameClaimType = "name";
    });

builder.Services.AddAuthorization(options =>
    options.AddPolicy(Bff.PoliticaSesion, policy => policy
        .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()));

builder.Services.AddProxyConfiable(builder.Configuration);
builder.Services.AddProxyPaciente(bff);
builder.Services.AddProblemDetails();

// SPEC 13: /health/vivo for the external uptime monitor on pacientes.<dominio>. Liveness only: the BFF answers.
builder.Services.AddHealthChecks();

WebApplication app = builder.Build();

// SPEC 13: first, so the scheme, host and client IP are Nginx's view of the request.
app.UseForwardedHeaders();

app.UseSerilogRequestLogging();

// Before the authentication: a path out of the prefix is 404 and a request without the header is 403, session or not.
app.UseGuardasBff();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/vivo", new HealthCheckOptions { ResponseWriter = (_, _) => Task.CompletedTask });

app.MapBffEndpoints();
app.MapReverseProxy();

await app.RunAsync();

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the BFF in tests.</summary>
public partial class Program;
