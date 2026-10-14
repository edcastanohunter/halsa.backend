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
using Scalar.AspNetCore;
using Serilog;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.AddObservability();

WebApplication app = builder.Build();

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
app.UseAuthorization();

// The Worker answers only /health: no business endpoint, so /v1/* is 404.
if (rol.AtiendeApi())
{
    app.MapEndpoints(app.MapGroup("v1"));
}

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

await app.RunAsync();

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the API in tests.</summary>
public partial class Program;
