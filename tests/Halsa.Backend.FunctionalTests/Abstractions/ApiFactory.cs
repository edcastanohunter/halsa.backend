using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Halsa.Backend.FunctionalTests.Abstractions;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Container starts as postgres / postgres so we can run the role-creation script.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("halsa.backend")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private string _ownerConnectionString = null!;
    private string _appConnectionString = null!;

    /// <summary>Connection string using <c>halsa_owner</c> — tests seed their own rows with it.</summary>
    public string OwnerConnectionString => _ownerConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");

        // The API connects as halsa_app at runtime.
        builder.UseSetting("ConnectionStrings:Database",   _appConnectionString);
        // MigrationExtensions uses this string if present; in tests we apply migrations ourselves.
        builder.UseSetting("ConnectionStrings:Migrations", _ownerConnectionString);

        // Opciones de Keycloak necesarias para que TenantContextResolver compare el iss.
        builder.UseSetting("Keycloak:PersonalAuthority",   TestAuthenticationHandler.DefaultIssuer);
        builder.UseSetting("Keycloak:PacientesAuthority",  TestAuthenticationHandler.PacientesIssuer);
        builder.UseSetting("Keycloak:Audience",            "halsa-api");
        builder.UseSetting("Keycloak:ClientId",            "halsa-api");
        builder.UseSetting("Keycloak:RequireHttpsMetadata", "false");

        builder.ConfigureTestServices(services =>
        {
            // Stub identity: registered last, so it becomes the default scheme.
            services
                .AddAuthentication(TestAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName,
                    _ => { });

            // The outbox dispatcher would race with assertions; drain it explicitly instead.
            services.RemoveAll<IHostedService>();
        });
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        await _container.StartAsync();

        // ── Step 1: create roles ──────────────────────────────────────────────
        await _container.ExecScriptAsync(RolesInitScript);

        // ── Step 2: derive connection strings ─────────────────────────────────
        string host = _container.Hostname;
        int port = _container.GetMappedPublicPort(5432);
        const string database = "halsa.backend";

        _ownerConnectionString = $"Host={host};Port={port};Database={database};" +
                                 "Username=halsa_owner;Password=halsa_owner_dev;Include Error Detail=true";
        _appConnectionString   = $"Host={host};Port={port};Database={database};" +
                                 "Username=halsa_app;Password=halsa_app_dev;Include Error Detail=true";

        // ── Step 3: apply migrations as halsa_owner ───────────────────────────
        // ConfigureWebHost hasn't run yet (it runs on first Services access), so we
        // apply migrations before the factory boots the app.
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder
            .UseNpgsql(_ownerConnectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable(
                    Microsoft.EntityFrameworkCore.Migrations.HistoryRepository.DefaultTableName,
                    Schemas.Default);
            })
            .UseSnakeCaseNamingConvention();

        await using ApplicationDbContext migrationContext = new(optionsBuilder.Options, NullTenantContext.Instance);
        await migrationContext.Database.MigrateAsync();
    }

    // Explicit implementation: WebApplicationFactory already exposes ValueTask DisposeAsync(),
    // and two methods cannot differ by return type alone.
    async Task IAsyncLifetime.DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// Creates a <see cref="HttpClient"/> that injects the provided headers in every request.
    /// Typically used to supply <c>X-Test-Claims</c> for a specific actor persona.
    /// </summary>
    public HttpClient CreateClientWithHeaders(IReadOnlyDictionary<string, string> headers)
    {
        HttpClient client = CreateClient();
        foreach ((string key, string value) in headers)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }
        return client;
    }

    // ── Embedded init script ──────────────────────────────────────────────────
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

[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>;
