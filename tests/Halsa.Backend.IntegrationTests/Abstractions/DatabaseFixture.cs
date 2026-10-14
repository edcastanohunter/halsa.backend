using System.Security.Cryptography;
using Halsa.Backend.Application;
using Halsa.Backend.Application.Abstractions.Authentication;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain;
using Halsa.Backend.Infrastructure;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.Database.Clinico;
using Halsa.Backend.Infrastructure.EventSourcing;
using Halsa.Backend.Infrastructure.Proceso;
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
/// 2. Run <c>01-roles.sql</c> to create <c>halsa_owner</c>, <c>halsa_app</c>, <c>halsa_clinico</c>, <c>halsa_worker</c> and
///    <c>halsa_ingesta</c> and grant CREATE on the database to <c>halsa_owner</c>.
/// 3. Apply the EF Core migrations of both contexts as <c>halsa_owner</c> (creates schemas, tables,
///    grants and RLS), then <c>auditoria.asegurar_particiones(3)</c>.
/// 4. Register all application services connected as <c>halsa_app</c> (and <c>halsa_clinico</c> for the
///    clinical context), with a random in-memory KEK.
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

    private IConfiguration _configuration = null!;

    /// <summary>Connection string using <c>halsa_owner</c> — for tests that run DDL or seed data.</summary>
    public string OwnerConnectionString { get; private set; } = null!;

    /// <summary>Connection string using <c>halsa_app</c> — for tests that verify RLS behaviour.</summary>
    public string AppConnectionString { get; private set; } = null!;

    /// <summary>Connection string using <c>halsa_clinico</c> — the role of the clinical context (SPEC 03).</summary>
    public string ClinicalConnectionString { get; private set; } = null!;

    /// <summary>Connection string using <c>halsa_ingesta</c> — the role of the FURAT/FUREP ingestion (SPEC 05).</summary>
    public string IngestaConnectionString { get; private set; } = null!;

    /// <summary>Connection string using <c>halsa_worker</c> — the cross-tenant sweeps of the Worker (SPEC 09).</summary>
    public string WorkerConnectionString { get; private set; } = null!;

    /// <summary>
    /// Connection string using the <c>postgres</c> superuser, which bypasses RLS. Only for asserting on
    /// rows no application role can read, such as audit rows with <c>tenant_id = null</c>.
    /// </summary>
    public string SuperuserConnectionString => _container.GetConnectionString();

    /// <summary>The KEK of this run: 32 random bytes, never written anywhere.</summary>
    public string KekBase64 { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

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
        ClinicalConnectionString = $"Host={host};Port={port};Database={database};" +
                                   "Username=halsa_clinico;Password=halsa_clinico_dev;Include Error Detail=true";
        IngestaConnectionString = $"Host={host};Port={port};Database={database};" +
                                  "Username=halsa_ingesta;Password=halsa_ingesta_dev;Include Error Detail=true";
        WorkerConnectionString = $"Host={host};Port={port};Database={database};" +
                                 "Username=halsa_worker;Password=halsa_worker_dev;Include Error Detail=true";

        // ── Step 3: apply the migrations of both contexts as halsa_owner, then the audit partitions ─
        await DatabaseMigrator.MigrateAsync(OwnerConnectionString);
        await DatabaseMigrator.AsegurarParticionesAsync(OwnerConnectionString);

        // ── Step 4: register services connected as halsa_app ─────────────────
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"]   = AppConnectionString,
                ["ConnectionStrings:Migrations"] = OwnerConnectionString,
                ["ConnectionStrings:Clinical"]   = ClinicalConnectionString,
                ["ConnectionStrings:Ingesta"]    = IngestaConnectionString,
                ["ConnectionStrings:Worker"]     = WorkerConnectionString,
                ["Clinico:KekBase64"]            = KekBase64,
                ["Keycloak:PersonalAuthority"]   = "http://localhost:8080/realms/halsa",
                ["Keycloak:PacientesAuthority"]  = "http://localhost:8080/realms/halsa-pacientes",
                ["Keycloak:Audience"]            = "halsa-api",
                ["Keycloak:ClientId"]            = "halsa-api",
                ["Keycloak:RequireHttpsMetadata"] = "false"
            })
            .Build();

        Services = CrearServicios();
    }

    /// <summary>
    /// A new provider with the fixture's registrations plus <paramref name="ajustes"/>, for a test that needs to
    /// replace a dependency (a gateway that fails, say). The caller disposes it.
    /// </summary>
    public ServiceProvider CrearServicios(Action<IServiceCollection>? ajustes = null)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(_configuration);
        services.AddApplication();
        services.AddInfrastructure(_configuration, RolProceso.Ambos);

        // In integration tests there is no HTTP context, so the middleware never fills the scope's
        // TenantContextHolder and CurrentUser never sees a principal. CreateScope(actor) fills the holder
        // (the real TenantContext reads it, so ISystemTenantScope works as in production) and CurrentUser
        // reads the scope's TestActor. Without an actor, Resuelto = false.
        // The event store does not depend on it; RLS tests use CreateDbContext instead.
        services.AddScoped<TestActor>();
        services.RemoveAll<ICurrentUser>();
        services.AddScoped<ICurrentUser, TestCurrentUser>();

        // Override EventTypeRegistry to include test aggregate events alongside domain events.
        // The last registration wins when resolving IEventTypeRegistry as a single service.
        services.AddSingleton<IEventTypeRegistry>(_ => new EventTypeRegistry(
            typeof(DomainAssemblyMarker).Assembly,
            typeof(TestAggregate).Assembly));

        ajustes?.Invoke(services);

        return services.BuildServiceProvider();
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
    /// A scope whose <see cref="ITenantContext"/> and <see cref="ICurrentUser"/> are <paramref name="actor"/>,
    /// as for an authenticated request. Set before anything else resolves them.
    /// </summary>
    public AsyncServiceScope CreateScope(ITenantContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        AsyncServiceScope scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestActor>().Tenant = actor;

        if (actor.Resuelto)
        {
            scope.ServiceProvider.GetRequiredService<TenantContextHolder>()
                .Establecer(new TenantContextData(actor.Tipo, actor.Sub, actor.TenantId, actor.IpsId));
        }

        return scope;
    }

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

    /// <summary>
    /// Builds a <see cref="ClinicalDbContext"/> connected as <c>halsa_clinico</c> with the real
    /// <see cref="TenantSessionInterceptor"/>.
    /// </summary>
    public ClinicalDbContext CreateClinicalDbContext(ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<ClinicalDbContext>()
            .UseNpgsql(ClinicalConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TenantSessionInterceptor(tenantContext))
            .Options;

        return new ClinicalDbContext(options, tenantContext);
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

        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'halsa_ingesta') THEN
                CREATE ROLE halsa_ingesta LOGIN PASSWORD 'halsa_ingesta_dev';
            END IF;
        END$$;

        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'halsa_worker') THEN
                CREATE ROLE halsa_worker LOGIN PASSWORD 'halsa_worker_dev';
            END IF;
        END$$;

        GRANT CREATE ON DATABASE "halsa.backend" TO halsa_owner;
        """;
}

[CollectionDefinition(nameof(DatabaseCollection))]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>;
