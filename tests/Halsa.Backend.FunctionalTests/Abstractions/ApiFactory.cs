using System.Security.Cryptography;
using Halsa.Backend.Api.Endpoints;
using Halsa.Backend.Application.Abstractions.Archivos;
using Halsa.Backend.Infrastructure.Database;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
    private string _clinicalConnectionString = null!;
    private string _ingestaConnectionString = null!;
    private string _workerConnectionString = null!;

    // A random KEK per run (SPEC 03): never written anywhere.
    private readonly string _kekBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>The encrypted supports of this run (SPEC 07, <c>Archivos:Raiz</c>): a temporary folder.</summary>
    public string ArchivosRaiz { get; } = Path.Combine(Path.GetTempPath(), $"halsa-api-archivos-{Guid.NewGuid():N}");

    /// <summary>
    /// The antivirus every upload goes through (SPEC 07): clean unless a test says otherwise, and a test that changes
    /// it puts it back. No real clamd runs in the tests.
    /// </summary>
    public FakeAntivirus Antivirus { get; } = new();

    /// <summary>Connection string using <c>halsa_owner</c> — tests seed their own rows with it.</summary>
    public string OwnerConnectionString => _ownerConnectionString;

    /// <summary>
    /// Connection string using the <c>postgres</c> superuser, which bypasses RLS. Only for asserting on
    /// rows no application role can read, such as audit rows with <c>tenant_id = null</c>.
    /// </summary>
    public string SuperuserConnectionString => _container.GetConnectionString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");

        // SPEC 09: outside Development the role is required. The hosted services are removed below anyway.
        builder.UseSetting("Halsa:Rol",                    "Ambos");

        // The API connects as halsa_app at runtime.
        builder.UseSetting("ConnectionStrings:Database",   _appConnectionString);
        // MigrationExtensions uses this string if present; in tests we apply migrations ourselves.
        builder.UseSetting("ConnectionStrings:Migrations", _ownerConnectionString);
        // The clinical context connects as halsa_clinico.
        builder.UseSetting("ConnectionStrings:Clinical",   _clinicalConnectionString);
        // The FURAT/FUREP ingestion connects as halsa_ingesta (SPEC 05).
        builder.UseSetting("ConnectionStrings:Ingesta",    _ingestaConnectionString);
        // The Worker's cross-tenant sweeps connect as halsa_worker (SPEC 09).
        builder.UseSetting("ConnectionStrings:Worker",     _workerConnectionString);
        builder.UseSetting("Clinico:KekBase64",            _kekBase64);
        builder.UseSetting("Archivos:Raiz",                ArchivosRaiz);
        // Nothing listens there, so /health reports the antivirus as Degraded whatever runs on the host.
        builder.UseSetting("Antivirus:Host",               "127.0.0.1");
        builder.UseSetting("Antivirus:Puerto",             "9");

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

            // Test-only routes (SPEC 07): the step-up policy on its own.
            services.AddTransient<IEndpoint, StepUpProbeEndpoint>();

            services.RemoveAll<IAntivirusScanner>();
            services.AddSingleton<IAntivirusScanner>(Antivirus);

            // SPEC 13: lets a test pose as the trusted proxy (or as anyone else).
            services.AddSingleton<IStartupFilter, RemoteIpDePrueba>();
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
        _clinicalConnectionString = $"Host={host};Port={port};Database={database};" +
                                    "Username=halsa_clinico;Password=halsa_clinico_dev;Include Error Detail=true";
        _ingestaConnectionString = $"Host={host};Port={port};Database={database};" +
                                   "Username=halsa_ingesta;Password=halsa_ingesta_dev;Include Error Detail=true";
        _workerConnectionString = $"Host={host};Port={port};Database={database};" +
                                  "Username=halsa_worker;Password=halsa_worker_dev;Include Error Detail=true";

        // ── Step 3: apply the migrations of both contexts, then the audit partitions ──
        // ConfigureWebHost hasn't run yet (it runs on first Services access), so we
        // apply migrations before the factory boots the app.
        await DatabaseMigrator.MigrateAsync(_ownerConnectionString);
        await DatabaseMigrator.AsegurarParticionesAsync(_ownerConnectionString);
    }

    // Explicit implementation: WebApplicationFactory already exposes ValueTask DisposeAsync(),
    // and two methods cannot differ by return type alone.
    async Task IAsyncLifetime.DisposeAsync()
    {
        await _container.DisposeAsync();

        if (Directory.Exists(ArchivosRaiz))
        {
            Directory.Delete(ArchivosRaiz, recursive: true);
        }
    }

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

[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>;
