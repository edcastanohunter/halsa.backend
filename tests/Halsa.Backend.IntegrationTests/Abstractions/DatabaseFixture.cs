using Halsa.Backend.Application;
using Halsa.Backend.Infrastructure;
using Halsa.Backend.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Halsa.Backend.IntegrationTests.Abstractions;

/// <summary>
/// Spins up a real PostgreSQL in Docker. Integration tests that matter - concurrency, jsonb,
/// partial indexes, SKIP LOCKED - cannot be written against an in-memory provider.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("halsa.backend")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public ServiceProvider Services { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = _container.GetConnectionString(),
                ["Keycloak:Authority"] = "http://localhost:8080/realms/halsa.backend",
                ["Keycloak:Audience"] = "halsa.backend-api",
                ["Keycloak:ClientId"] = "halsa.backend-api",
                ["Keycloak:RequireHttpsMetadata"] = "false"
            })
            .Build();

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddApplication();
        services.AddInfrastructure(configuration);

        Services = services.BuildServiceProvider();

        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // The template ships without migrations on purpose: create yours with
        // `dotnet ef migrations add Initial`. Tests build the schema from the model instead.
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (Services is not null)
        {
            await Services.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();
}

[CollectionDefinition(nameof(DatabaseCollection))]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>;
