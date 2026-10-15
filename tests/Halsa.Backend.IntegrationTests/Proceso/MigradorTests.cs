using Halsa.Backend.Infrastructure.Proceso;
using Halsa.Backend.IntegrationTests.Abstractions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Halsa.Backend.IntegrationTests.Proceso;

/// <summary>
/// SPEC 13: <c>Halsa:Rol=Migrador</c> contra una base vacía aplica todo y termina con <c>0</c>, y una segunda ejecución
/// también, sin cambios. Su propio contenedor: el de <see cref="DatabaseFixture"/> ya viene migrado.
/// </summary>
public sealed class MigradorTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("halsa.backend")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await _container.ExecScriptAsync(DatabaseFixture.RolesInitScript);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    [Fact]
    public async Task EmptyDatabase_ShouldBeMigrated_AndASecondRunShouldChangeNothing()
    {
        IConfiguration configuracion = Configuracion(OwnerConnectionString());

        using var primera = new StringWriter();
        (await Migrador.EjecutarAsync(configuracion, primera)).ShouldBe(Migrador.Exito);

        (long migraciones, long particiones) antes = await EstadoAsync();
        antes.migraciones.ShouldBeGreaterThan(0);
        antes.particiones.ShouldBeGreaterThan(0);

        using var segunda = new StringWriter();
        (await Migrador.EjecutarAsync(configuracion, segunda)).ShouldBe(Migrador.Exito);

        (await EstadoAsync()).ShouldBe(antes);
    }

    [Fact]
    public async Task WithoutTheMigrationsConnectionString_ShouldExitWith1()
    {
        using var salida = new StringWriter();

        (await Migrador.EjecutarAsync(Configuracion(null), salida)).ShouldBe(Migrador.Fallo);
        salida.ToString().ShouldContain("Migrations");
    }

    [Fact]
    public async Task WhenTheDatabaseRejectsTheConnection_ShouldExitWith1()
    {
        string sinPermiso = new NpgsqlConnectionStringBuilder(OwnerConnectionString()) { Password = "incorrecta" }.ToString();
        using var salida = new StringWriter();

        (await Migrador.EjecutarAsync(Configuracion(sinPermiso), salida)).ShouldBe(Migrador.Fallo);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string OwnerConnectionString() =>
        $"Host={_container.Hostname};Port={_container.GetMappedPublicPort(5432)};Database=halsa.backend;" +
        "Username=halsa_owner;Password=halsa_owner_dev";

    private static IConfiguration Configuracion(string? migrations) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Migrations"] = migrations })
        .Build();

    /// <summary>The applied migrations of both contexts and the partitions of <c>auditoria.accesos</c>.</summary>
    private async Task<(long, long)> EstadoAsync()
    {
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT count(*) FROM app."__EFMigrationsHistory")
                 + (SELECT count(*) FROM clinico.__ef_migrations_history),
                   (SELECT count(*) FROM pg_inherits WHERE inhparent = 'auditoria.accesos'::regclass)
            """;
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
