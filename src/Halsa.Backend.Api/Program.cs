using System.Reflection;
using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Api.Middleware;
using Halsa.Backend.Application;
using Halsa.Backend.Infrastructure;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using Serilog;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

builder.Services.AddEndpoints(Assembly.GetExecutingAssembly());

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.AddObservability();

WebApplication app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();

    await app.ApplyMigrationsAsync();
}

app.UseSerilogRequestLogging();
app.UseMiddleware<RequestContextLoggingMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapEndpoints();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

await app.RunAsync();

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot the API in tests.</summary>
public partial class Program;
