using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.Infrastructure.Worker;
using Halsa.Backend.Infrastructure.Worker.Jobs;
using Halsa.Backend.IntegrationTests.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;
using static Halsa.Backend.IntegrationTests.Integraciones.IngestaSql;
using static Halsa.Backend.IntegrationTests.Terminos.PlazosSql;

namespace Halsa.Backend.IntegrationTests.Terminos;

/// <summary>
/// SPEC 09, paso 7: el vencimiento de la controversia pone el dictamen en firme, y <see cref="RecalcularPlazosJob"/>
/// alarga los plazos con un festivo nuevo sin acortarlos nunca.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class VencimientoYRecalculoTests(DatabaseFixture db)
{
    [Fact]
    public async Task ExpiredControversy_ShouldLeaveTheDictamenEnFirme_AuditedAsTheSystem()
    {
        const string tenant = "ARL_VR_FIRME";
        var notificado = new DateTimeOffset(2031, 5, 2, 9, 0, 0, Bogota);
        (Guid caso, _) = await InsertCasoAsync(db, tenant, notificado.AddMonths(-6), notificado.AddMonths(-6));
        Guid dictamen = await InsertDictamenNotificadoAsync(tenant, caso, notificado);
        Guid plazo = await InsertPlazoAsync(db, tenant, caso, "CONTROVERSIA_DICTAMEN", notificado.AddDays(14),
            notificado.AddDays(9), iniciaEn: notificado, referenciaId: dictamen);

        await Barrido(notificado.AddDays(15)).BarrerAsync(CancellationToken.None);
        await EntregarVencidoAsync(plazo);
        // At-least-once: the same event again changes nothing.
        await EntregarVencidoAsync(plazo);

        (await ContarAsync(db, """
            SELECT count(*) FROM atel.dictamenes_pcl WHERE id = @d AND estado_notificacion = 'EN_FIRME'
            """, ("d", dictamen))).ShouldBe(1);
        (await ContarAsync(db, """
            SELECT count(*) FROM auditoria.accesos
            WHERE caso_medico_id = @caso AND accion = 'ACTUALIZAR' AND recurso_tipo = 'CASO'
              AND actor_tipo = 'sistema' AND actor_id = 'sistema' AND tenant_id = @tenant
            """, ("caso", caso), ("tenant", tenant))).ShouldBe(1);
    }

    [Fact]
    public async Task AnotherExpiredRule_ShouldNotTouchTheDictamen()
    {
        const string tenant = "ARL_VR_OTRA";
        var notificado = new DateTimeOffset(2031, 5, 2, 9, 0, 0, Bogota);
        (Guid caso, _) = await InsertCasoAsync(db, tenant, notificado.AddMonths(-6), notificado.AddMonths(-6));
        Guid dictamen = await InsertDictamenNotificadoAsync(tenant, caso, notificado);
        Guid plazo = await InsertPlazoAsync(db, tenant, caso, "INVESTIGACION_AT", notificado.AddDays(14),
            notificado.AddDays(9), referenciaId: dictamen);

        await Barrido(notificado.AddDays(15)).BarrerAsync(CancellationToken.None);
        await EntregarVencidoAsync(plazo);

        (await ContarAsync(db, """
            SELECT count(*) FROM atel.dictamenes_pcl WHERE id = @d AND estado_notificacion = 'NOTIFICADO'
            """, ("d", dictamen))).ShouldBe(1);
    }

    [Fact]
    public async Task ManualHolidayInsideAnOpenPlazo_ShouldExtendItOneDay_AndRemovingItShouldNotShortenIt()
    {
        // 2099: no other test loads holidays for it.
        const string tenant = "ARL_VR_RECALCULO";
        var inicia = new DateTimeOffset(2099, 3, 2, 9, 0, 0, Bogota);
        var sinFestivos = new CalculadoraTerminos([]);
        DateTimeOffset vence = sinFestivos.Vencimiento(inicia, 10, UnidadTermino.Habiles);
        DateOnly festivo = CalculadoraTerminos.FechaLocal(sinFestivos.Vencimiento(inicia, 3, UnidadTermino.Habiles));

        (Guid caso, _) = await InsertCasoAsync(db, tenant, inicia.AddDays(-30), inicia.AddDays(-30));
        Guid plazo = await InsertPlazoAsync(db, tenant, caso, "CONTROVERSIA_DICTAMEN", vence,
            sinFestivos.Alerta(vence, 3, UnidadTermino.Habiles), iniciaEn: inicia, referenciaId: Guid.CreateVersion7());

        await EjecutarComoSuperusuarioAsync(db,
            "INSERT INTO sst.festivos (fecha, descripcion, origen) VALUES (@f, 'Decreto de prueba', 'MANUAL')", ("f", festivo));
        await Recalculo().RecalcularAsync(CancellationToken.None);

        var conFestivo = new CalculadoraTerminos([festivo]);
        DateTimeOffset alargado = conFestivo.Vencimiento(inicia, 10, UnidadTermino.Habiles);

        // One working day later.
        DateOnly siguienteHabil = CalculadoraTerminos.FechaLocal(sinFestivos.Vencimiento(vence, 1, UnidadTermino.Habiles));
        CalculadoraTerminos.FechaLocal(alargado).ShouldBe(siguienteHabil);

        PlazoFila fila = await PlazoAsync(db, plazo);
        fila.VenceEn.ShouldBe(alargado);
        fila.AlertaEn.ShouldBe(conFestivo.Alerta(alargado, 3, UnidadTermino.Habiles));

        await EjecutarComoSuperusuarioAsync(db, "DELETE FROM sst.festivos WHERE fecha = @f", ("f", festivo));
        await Recalculo().RecalcularAsync(CancellationToken.None);

        (await PlazoAsync(db, plazo)).VenceEn.ShouldBe(alargado);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private BarridoPlazosJob Barrido(DateTimeOffset ahora) => new(
        db.Services.GetRequiredService<BaseDatosWorker>(), new FakeTimeProvider(ahora), NullLogger<BarridoPlazosJob>.Instance);

    private RecalcularPlazosJob Recalculo() => new(
        db.Services.GetRequiredService<BaseDatosWorker>(), NullLogger<RecalcularPlazosJob>.Instance);

    /// <summary>Delivers the <c>PlazoVencidoIntegrationEvent</c> of <paramref name="plazo"/> as the outbox would.</summary>
    private async Task EntregarVencidoAsync(Guid plazo)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.SuperuserConnectionString);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT type, content::text FROM outbox.outbox_messages
            WHERE type LIKE '%PlazoVencidoIntegrationEvent' AND (content->>'plazoId')::uuid = @plazo
            """;
        cmd.Parameters.AddWithValue("plazo", plazo);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        string tipo = reader.GetString(0);
        string contenido = reader.GetString(1);

        await db.Services.GetRequiredService<IEventBus>().PublishAsync(tipo, contenido);
    }

    private async Task<Guid> InsertDictamenNotificadoAsync(string tenant, Guid caso, DateTimeOffset notificado)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenant);

        Guid id = Guid.CreateVersion7();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO atel.dictamenes_pcl
              (id, tenant_id, caso_medico_id, registro_clinico_id, porcentaje_pcl, origen, fecha_estructuracion,
               fecha_dictamen, calificador_sub, calificador_registro_rethus, estado_notificacion, notificado_en,
               medio_notificacion)
            VALUES
              (@id, @tid, @caso, gen_random_uuid(), 25.00, 'LABORAL', @fecha, @fecha, 'sub-calificador', 'RM-1',
               'NOTIFICADO', @notificado, 'PERSONAL')
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", tenant);
        cmd.Parameters.AddWithValue("caso", caso);
        cmd.Parameters.AddWithValue("fecha", DateOnly.FromDateTime(notificado.AddDays(-5).DateTime));
        cmd.Parameters.AddWithValue("notificado", notificado.ToUniversalTime());
        await cmd.ExecuteNonQueryAsync();

        return id;
    }
}
