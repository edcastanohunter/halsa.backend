using Halsa.Backend.Application;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain;
using Halsa.Backend.Infrastructure;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.EventSourcing;
using Halsa.Backend.Infrastructure.Tenancy;
using Halsa.Backend.SharedKernel.EventSourcing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Halsa.Backend.IntegrationTests.Abstractions;

/// <summary>
/// Spins up a real PostgreSQL 17 instance in Docker.
///
/// Lifecycle:
/// 1. Start the container as the <c>postgres</c> superuser.
/// 2. Run <c>01-roles.sql</c> to create <c>halsa_owner</c>, <c>halsa_app</c> and
///    <c>halsa_clinico</c> and grant CREATE on the database to <c>halsa_owner</c>.
/// 3. Apply EF Core migrations as <c>halsa_owner</c> (creates schemas, tables, grants and RLS).
/// 4. Register all application services connected as <c>halsa_app</c>.
///
/// Integration tests that matter — concurrency, jsonb, partial indexes, SKIP LOCKED, RLS — cannot
/// be written against an in-memory provider.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    // Container starts as postgres / postgres so that we can run the role-creation script.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("halsa.backend")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public ServiceProvider Services { get; private set; } = null!;

    /// <summary>Connection string using <c>halsa_owner</c> — for tests that run DDL or seed data.</summary>
    public string OwnerConnectionString { get; private set; } = null!;

    /// <summary>Connection string using <c>halsa_app</c> — for tests that verify RLS behaviour.</summary>
    public string AppConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // ── Step 1: create roles and grant database-level CREATE ─────────────
        await _container.ExecScriptAsync(RolesInitScript);

        // ── Step 2: build derived connection strings ──────────────────────────
        // _container.GetConnectionString() returns something like
        //   "Host=localhost;Port=NNNNN;Database=halsa.backend;Username=postgres;Password=postgres"
        // We derive the owner and app strings by replacing credentials.
        string host = _container.Hostname;
        int port = _container.GetMappedPublicPort(5432);
        const string database = "halsa.backend";

        OwnerConnectionString = $"Host={host};Port={port};Database={database};" +
                                "Username=halsa_owner;Password=halsa_owner_dev;Include Error Detail=true";
        AppConnectionString   = $"Host={host};Port={port};Database={database};" +
                                "Username=halsa_app;Password=halsa_app_dev;Include Error Detail=true";

        // ── Step 3: apply EF Core migrations as halsa_owner ──────────────────
        {
            var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
            optionsBuilder
                .UseNpgsql(OwnerConnectionString, npgsql =>
                {
                    npgsql.MigrationsHistoryTable(
                        Microsoft.EntityFrameworkCore.Migrations.HistoryRepository.DefaultTableName,
                        Schemas.Default);
                })
                .UseSnakeCaseNamingConvention();

            await using ApplicationDbContext migrationContext = new(optionsBuilder.Options, NullTenantContext.Instance);
            await migrationContext.Database.MigrateAsync();
        }

        // ── Step 4: register services connected as halsa_app ─────────────────
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"]   = AppConnectionString,
                ["ConnectionStrings:Migrations"] = OwnerConnectionString,
                ["Keycloak:PersonalAuthority"]   = "http://localhost:8080/realms/halsa",
                ["Keycloak:PacientesAuthority"]  = "http://localhost:8080/realms/halsa-pacientes",
                ["Keycloak:Audience"]            = "halsa-api",
                ["Keycloak:ClientId"]            = "halsa-api",
                ["Keycloak:RequireHttpsMetadata"] = "false"
            })
            .Build();

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddApplication();
        services.AddInfrastructure(configuration);

        // In integration tests there is no HTTP context, so HttpTenantContext (registered by
        // AddInfrastructure) would fail to resolve IHttpContextAccessor.
        // Replace it with NullTenantContext (Resuelto = false). The event store does not depend
        // on it; atel tables stay invisible, so RLS tests use CreateDbContext instead.
        services.RemoveAll<ITenantContext>();
        services.AddScoped<ITenantContext>(_ => NullTenantContext.Instance);

        // Override EventTypeRegistry to include test aggregate events alongside domain events.
        // The last registration wins when resolving IEventTypeRegistry as a single service.
        services.AddSingleton<IEventTypeRegistry>(_ => new EventTypeRegistry(
            typeof(DomainAssemblyMarker).Assembly,
            typeof(TestAggregate).Assembly));

        Services = services.BuildServiceProvider();
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

    /// <summary>
    /// Builds an <see cref="ApplicationDbContext"/> connected as <c>halsa_app</c> with the real
    /// <see cref="TenantSessionInterceptor"/>, as the API does for a request of <paramref name="tenantContext"/>.
    /// </summary>
    public ApplicationDbContext CreateDbContext(ITenantContext tenantContext, string? connectionString = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString ?? AppConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor(tenantContext))
            .Options;

        return new ApplicationDbContext(options, tenantContext);
    }

    // ── Embedded init script ──────────────────────────────────────────────────
    // Mirrors docker/postgres/init/01-roles.sql so Testcontainers does not need
    // to reach the host filesystem.
    private const string RolesInitScript = """
        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'halsa_owner') THEN
                CREATE ROLE halsa_owner LOGIN PASSWORD 'halsa_owner_dev' CREATEDB;
            END IF;
        END$$;

        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'halsa_app') THEN
                CREATE ROLE halsa_app LOGIN PASSWORD 'halsa_app_dev';
            END IF;
        END$$;

        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'halsa_clinico') THEN
                CREATE ROLE halsa_clinico LOGIN PASSWORD 'halsa_clinico_dev';
            END IF;
        END$$;

        GRANT CREATE ON DATABASE "halsa.backend" TO halsa_owner;
        """;
}

[CollectionDefinition(nameof(DatabaseCollection))]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>;
