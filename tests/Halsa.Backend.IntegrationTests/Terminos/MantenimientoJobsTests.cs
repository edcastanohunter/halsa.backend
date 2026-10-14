using Halsa.Backend.Infrastructure.Archivos;
using Halsa.Backend.Infrastructure.Worker;
using Halsa.Backend.Infrastructure.Worker.Jobs;
using Halsa.Backend.IntegrationTests.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;
using static Halsa.Backend.IntegrationTests.Integraciones.IngestaSql;

namespace Halsa.Backend.IntegrationTests.Terminos;

/// <summary>SPEC 09, paso 8: los jobs de mantenimiento, como <c>halsa_worker</c>.</summary>
[Collection(nameof(DatabaseCollection))]
public sealed class MantenimientoJobsTests(DatabaseFixture db) : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), $"halsa-huerfanos-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_raiz))
        {
            Directory.Delete(_raiz, recursive: true);
        }
    }

    [Fact]
    public async Task PurgaIdempotencia_ShouldDeleteAnExpiredKey_AndKeepAValidOne()
    {
        const string tenant = "ARL_MANT_PURGA";
        Guid vencida = await InsertLlaveAsync(tenant, DateTimeOffset.UtcNow.AddMinutes(-1));
        Guid vigente = await InsertLlaveAsync(tenant, DateTimeOffset.UtcNow.AddHours(1));

        int borradas = await new PurgaIdempotenciaJob(Worker(), NullLogger<PurgaIdempotenciaJob>.Instance)
            .PurgarAsync(CancellationToken.None);

        borradas.ShouldBeGreaterThanOrEqualTo(1);
        (await LlaveExisteAsync(vencida)).ShouldBeFalse();
        (await LlaveExisteAsync(vigente)).ShouldBeTrue();
    }

    [Fact]
    public async Task ArchivosHuerfanos_ShouldDeleteAnOrphanOf2Hours_AndKeepOneWithMetadataAndARecentOrphan()
    {
        const string tenant = "ARL_MANT_HUERFANOS";
        DateTimeOffset ahora = DateTimeOffset.UtcNow;

        string huerfanoViejo = Archivo($"{tenant}/2026/10/{Guid.CreateVersion7()}.bin", ahora.AddHours(-2));
        string huerfanoReciente = Archivo($"{tenant}/2026/10/{Guid.CreateVersion7()}.bin", ahora.AddMinutes(-10));
        string rutaConMetadata = $"{tenant}/2026/10/{Guid.CreateVersion7()}.bin";
        string conMetadata = Archivo(rutaConMetadata, ahora.AddHours(-2));
        await InsertSoporteAsync(tenant, rutaConMetadata);

        var job = new ArchivosHuerfanosJob(
            Worker(),
            Options.Create(new ArchivosOptions { Raiz = _raiz }),
            new FakeTimeProvider(ahora),
            NullLogger<ArchivosHuerfanosJob>.Instance);

        (await job.BorrarAsync(CancellationToken.None)).ShouldBe(1);

        File.Exists(huerfanoViejo).ShouldBeFalse();
        File.Exists(huerfanoReciente).ShouldBeTrue();
        File.Exists(conMetadata).ShouldBeTrue();
    }

    [Fact]
    public async Task ArchivosHuerfanos_WithoutTheFolder_ShouldDoNothing()
    {
        var job = new ArchivosHuerfanosJob(
            Worker(),
            Options.Create(new ArchivosOptions { Raiz = Path.Combine(_raiz, "no-existe") }),
            TimeProvider.System,
            NullLogger<ArchivosHuerfanosJob>.Instance);

        (await job.BorrarAsync(CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task ParticionesAuditoria_ShouldEnsureThePartitionThreeMonthsAhead()
    {
        await new ParticionesAuditoriaJob(Worker()).AsegurarAsync(CancellationToken.None);

        DateTime mes = DateTime.UtcNow.AddMonths(3);
        string particion = $"auditoria.accesos_{mes:yyyy_MM}";
        (await ContarAsync(db, "SELECT count(*) FROM pg_class WHERE oid = to_regclass(@p)", ("p", particion))).ShouldBe(1);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private BaseDatosWorker Worker() => db.Services.GetRequiredService<BaseDatosWorker>();

    private string Archivo(string rutaRelativa, DateTimeOffset modificado)
    {
        string absoluta = Path.Combine(_raiz, rutaRelativa);
        Directory.CreateDirectory(Path.GetDirectoryName(absoluta)!);
        File.WriteAllBytes(absoluta, [1, 2, 3]);
        File.SetLastWriteTimeUtc(absoluta, modificado.UtcDateTime);
        return absoluta;
    }

    private async Task<Guid> InsertLlaveAsync(string tenant, DateTimeOffset expiraEn)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenant);

        Guid llave = Guid.NewGuid();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO integracion.solicitudes_idempotentes
              (emisor, idempotency_key, tenant_id, cuerpo_sha256, solicitud_id, respuesta, creada_en, expira_en)
            VALUES ('tenant:' || @t, @k, @t, '\x00'::bytea, gen_random_uuid(), '{}'::jsonb, now(), @expira)
            """;
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("k", llave);
        cmd.Parameters.AddWithValue("expira", expiraEn.ToUniversalTime());
        await cmd.ExecuteNonQueryAsync();

        return llave;
    }

    private async Task<bool> LlaveExisteAsync(Guid llave) => await ContarAsync(db,
        "SELECT count(*) FROM integracion.solicitudes_idempotentes WHERE idempotency_key = @k", ("k", llave)) == 1;

    private async Task InsertSoporteAsync(string tenant, string ruta)
    {
        Guid caso = await InsertCasoAsync(db, tenant);
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenant);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO clinico.soportes
              (id, tenant_id, caso_medico_id, tipo, ruta, key_id, sha256_claro, tamano, subido_en, retener_hasta)
            VALUES (gen_random_uuid(), @t, @caso, 'INCAPACIDAD', @ruta, 'local:v1', '\x00'::bytea, 3, now(), DATE '2046-01-01')
            """;
        cmd.Parameters.AddWithValue("t", tenant);
        cmd.Parameters.AddWithValue("caso", caso);
        cmd.Parameters.AddWithValue("ruta", ruta);
        await cmd.ExecuteNonQueryAsync();
    }
}
