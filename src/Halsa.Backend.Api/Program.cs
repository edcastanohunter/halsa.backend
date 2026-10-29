using System.Reflection;
using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Extensions.Seed;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Api.Middleware;
using Halsa.Backend.Application;
using Halsa.Backend.Infrastructure;
using Halsa.Backend.Infrastructure.Proceso;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using Serilog;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// SPEC 13: the Migrador applies the migrations and exits with 0 or 1; it never builds the host.
if (RolesProceso.EsMigrador(builder.Configuration))
{
    return await Migrador.EjecutarAsync(builder.Configuration, Console.Out);
}

// SPEC 13: a development shortcut in Production stops the start with 1 and names the condition.
if (builder.Environment.IsProduction())
{
    IReadOnlyList<string> fallas = ProductionGuard.Verificar(builder.Configuration);
    if (fallas.Count > 0)
    {
        return ProductionGuard.Abortar(fallas, Console.Error);
    }
}

builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

// SPEC 09: the same image runs as the API, the Worker or both. The role decides what is registered.
RolProceso rol = RolesProceso.Resolver(builder.Configuration, builder.Environment.EnvironmentName);

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration, rol);

if (rol.AtiendeApi())
{
    builder.Services.AddEndpoints(Assembly.GetExecutingAssembly());
}

builder.Services.AddProxyConfiable(builder.Configuration);
builder.Services.AddCorsBackoffice(builder.Configuration);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.AddObservability();

WebApplication app = builder.Build();

if (app.Environment.IsProduction())
{
    IReadOnlyList<string> fallas = ProductionGuard.VerificarServicios(app.Services);
    if (fallas.Count > 0)
    {
        return ProductionGuard.Abortar(fallas, Console.Error);
    }
}

// SPEC 13: first, so everything after it sees the client's IP and scheme, and no forged X-Client-Cert.
app.UseProxyConfiable();

// Before everything that can answer: the preflight carries no token, and a 401/403/500 must still reach the SPA.
app.UseCorsBackoffice(app.Configuration);

app.UseExceptionHandler();

// A Worker started on its own in Development expects the API (or Ambos) to have migrated.
if (app.Environment.IsDevelopment() && rol.AtiendeApi())
{
    app.MapOpenApi();
    app.MapScalarApiReference();

    await app.ApplyMigrationsAsync();
    await app.SeedDevDataAsync();
}

app.UseSerilogRequestLogging();
app.UseMiddleware<RequestContextLoggingMiddleware>();

app.UseAuthentication();
app.UseMiddleware<TenantContextMiddleware>();
app.UseMiddleware<IntegrationCertificateBindingMiddleware>();
app.UseAuthorization();

// The Worker answers only /health: no business endpoint, so /v1/* is 404.
if (rol.AtiendeApi())
{
    app.MapEndpoints(app.MapGroup("v1"));
}

// The detailed report: only from the internal network, Nginx never routes it.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

// SPEC 13: the public probe of the external uptime monitor. Only 200 or 503, no detail; Degraded is still alive.
app.MapHealthChecks("/health/vivo", new HealthCheckOptions
{
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    },
    ResponseWriter = (_, _) => Task.CompletedTask
});

await app.RunAsync();

return 0;

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the API in tests.</summary>
public partial class Program;
