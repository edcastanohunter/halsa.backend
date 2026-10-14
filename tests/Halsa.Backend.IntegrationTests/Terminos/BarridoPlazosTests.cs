using Halsa.Backend.Infrastructure.Worker;
using Halsa.Backend.Infrastructure.Worker.Jobs;
using Halsa.Backend.IntegrationTests.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Halsa.Backend.IntegrationTests.Integraciones.IngestaSql;
using static Halsa.Backend.IntegrationTests.Terminos.PlazosSql;

namespace Halsa.Backend.IntegrationTests.Terminos;

/// <summary>
/// SPEC 09, paso 6: <see cref="BarridoPlazosJob"/> como <c>halsa_worker</c>, con un reloj falso. El barrido es global,
/// así que cada test cuenta solo los eventos de sus propios plazos.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class BarridoPlazosTests(DatabaseFixture db)
{
    private static readonly DateTimeOffset Ahora = new(2032, 3, 10, 10, 0, 0, Bogota);

    [Fact]
    public async Task AtAlertaEn_ShouldEmitExactlyOneProximoAVencer_AndASecondSweepNone()
    {
        Guid plazo = await NuevoPlazoAsync("ARL_BAR_ALERTA", venceEn: Ahora.AddDays(3), alertaEn: Ahora);

        await Job(Ahora).BarrerAsync(CancellationToken.None);
        await Job(Ahora.AddMinutes(1)).BarrerAsync(CancellationToken.None);

        (await EventosAsync("PlazoProximoAVencerIntegrationEvent", plazo)).ShouldBe(1);
        (await EventosAsync("PlazoVencidoIntegrationEvent", plazo)).ShouldBe(0);

        PlazoFila fila = await PlazoAsync(db, plazo);
        fila.Estado.ShouldBe("ABIERTO");
        fila.AlertadoEn.ShouldBe(Ahora);
    }

    [Fact]
    public async Task BeforeAlertaEn_ShouldEmitNothing()
    {
        Guid plazo = await NuevoPlazoAsync("ARL_BAR_ANTES", venceEn: Ahora.AddDays(3), alertaEn: Ahora.AddSeconds(1));

        await Job(Ahora).BarrerAsync(CancellationToken.None);

        (await EventosAsync("PlazoProximoAVencerIntegrationEvent", plazo)).ShouldBe(0);
        (await PlazoAsync(db, plazo)).AlertadoEn.ShouldBeNull();
    }

    [Fact]
    public async Task AfterVenceEn_ShouldBecomeVencido_WithOnePlazoVencidoEvent()
    {
        Guid plazo = await NuevoPlazoAsync("ARL_BAR_VENCE", venceEn: Ahora.AddSeconds(-1), alertaEn: Ahora.AddDays(-3));

        await Job(Ahora).BarrerAsync(CancellationToken.None);
        await Job(Ahora.AddMinutes(1)).BarrerAsync(CancellationToken.None);

        (await PlazoAsync(db, plazo)).Estado.ShouldBe("VENCIDO");
        (await EventosAsync("PlazoVencidoIntegrationEvent", plazo)).ShouldBe(1);
        (await ContarAsync(db, """
            SELECT count(*) FROM outbox.outbox_messages
            WHERE type LIKE '%PlazoVencidoIntegrationEvent' AND (content->>'plazoId')::uuid = @plazo
              AND content->>'tenantId' = 'ARL_BAR_VENCE' AND content->>'radicado' = 'ATEL-BARRIDO'
              AND content->'plazo'->>'regla' = 'INVESTIGACION_AT'
              AND content->'plazo'->>'baseLegal' = 'Res. 1401/2007; Decreto 1072/2015, art. 2.2.4.6.32'
            """, ("plazo", plazo))).ShouldBe(1);
    }

    [Fact]
    public async Task HalsaWorker_ShouldSweepThePlazosOfEveryTenant()
    {
        Guid plazoA = await NuevoPlazoAsync("ARL_BAR_TENANT_A", venceEn: Ahora.AddHours(-1), alertaEn: Ahora.AddDays(-3));
        Guid plazoB = await NuevoPlazoAsync("ARL_BAR_TENANT_B", venceEn: Ahora.AddHours(-1), alertaEn: Ahora.AddDays(-3));

        await Job(Ahora).BarrerAsync(CancellationToken.None);

        (await PlazoAsync(db, plazoA)).Estado.ShouldBe("VENCIDO");
        (await PlazoAsync(db, plazoB)).Estado.ShouldBe("VENCIDO");
    }

    [Fact]
    public async Task TwoWorkersInParallel_ShouldEmitExactlyOneEventPerExpiredPlazo()
    {
        const string tenant = "ARL_BAR_PARALELO";
        (Guid caso, _) = await InsertCasoAsync(db, tenant, Ahora.AddDays(-30), Ahora.AddDays(-30));
        var plazos = new List<Guid>();
        for (int i = 0; i < 60; i++)
        {
            // One case, so the reference keeps them unique.
            plazos.Add(await InsertPlazoAsync(db, tenant, caso, "INVESTIGACION_AT", Ahora.AddMinutes(-i - 1),
                Ahora.AddDays(-3), referenciaId: Guid.CreateVersion7(), radicado: "ATEL-PARALELO"));
        }

        await Task.WhenAll(
            Task.Run(() => Job(Ahora).BarrerAsync(CancellationToken.None)),
            Task.Run(() => Job(Ahora).BarrerAsync(CancellationToken.None)));

        (await ContarAsync(db, """
            SELECT count(*) FROM sst.plazos WHERE id = ANY(@ids) AND estado = 'VENCIDO'
            """, ("ids", plazos.ToArray()))).ShouldBe(60);
        (await ContarAsync(db, """
            SELECT count(*) FROM (
                SELECT (content->>'plazoId')::uuid AS plazo, count(*) AS n FROM outbox.outbox_messages
                WHERE type LIKE '%PlazoVencidoIntegrationEvent' AND (content->>'plazoId')::uuid = ANY(@ids)
                GROUP BY 1) e
            WHERE e.n = 1
            """, ("ids", plazos.ToArray()))).ShouldBe(60);
        (await ContarAsync(db, """
            SELECT count(*) FROM outbox.outbox_messages
            WHERE type LIKE '%PlazoVencidoIntegrationEvent' AND (content->>'plazoId')::uuid = ANY(@ids)
            """, ("ids", plazos.ToArray()))).ShouldBe(60);
    }

    [Fact]
    public async Task TopeOfAChainClosedMoreThan30DaysAgo_ShouldBeCumplido_AndARecentOneStayOpen()
    {
        const string tenant = "ARL_BAR_TOPE";
        DateOnly hoy = DateOnly.FromDateTime(Ahora.DateTime);
        Guid cerrada = await NuevoPlazoAsync(tenant, Ahora.AddDays(100), Ahora.AddDays(70), "TOPE_IT_180",
            referenciaId: Guid.CreateVersion7(), ultimaFechaFin: hoy.AddDays(-31));
        Guid reciente = await NuevoPlazoAsync(tenant, Ahora.AddDays(100), Ahora.AddDays(70), "TOPE_IT_180",
            referenciaId: Guid.CreateVersion7(), ultimaFechaFin: hoy.AddDays(-30));

        await Job(Ahora).BarrerAsync(CancellationToken.None);

        PlazoFila fila = await PlazoAsync(db, cerrada);
        fila.Estado.ShouldBe("CUMPLIDO");
        fila.Nota.ShouldBe("cadena cerrada antes del tope");
        fila.CumplidoPor.ShouldBe("sistema");
        fila.CumplidoEn.ShouldBe(Ahora);
        (await PlazoAsync(db, reciente)).Estado.ShouldBe("ABIERTO");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private BarridoPlazosJob Job(DateTimeOffset ahora) => new(
        db.Services.GetRequiredService<BaseDatosWorker>(),
        new FakeTimeProvider(ahora),
        NullLogger<BarridoPlazosJob>.Instance);

    private async Task<Guid> NuevoPlazoAsync(
        string tenant,
        DateTimeOffset venceEn,
        DateTimeOffset alertaEn,
        string regla = "INVESTIGACION_AT",
        Guid? referenciaId = null,
        DateOnly? ultimaFechaFin = null)
    {
        (Guid caso, _) = await InsertCasoAsync(db, tenant, Ahora.AddDays(-30), Ahora.AddDays(-30));
        return await InsertPlazoAsync(db, tenant, caso, regla, venceEn, alertaEn,
            referenciaId: referenciaId, ultimaFechaFin: ultimaFechaFin, radicado: "ATEL-BARRIDO");
    }

    private Task<long> EventosAsync(string tipo, Guid plazo) => ContarAsync(db, $"""
        SELECT count(*) FROM outbox.outbox_messages
        WHERE type LIKE '%{tipo}' AND (content->>'plazoId')::uuid = @plazo
        """, ("plazo", plazo));
}
