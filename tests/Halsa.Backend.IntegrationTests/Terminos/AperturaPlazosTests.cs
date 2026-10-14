using Halsa.Backend.Application.Dictamenes;
using Halsa.Backend.Application.Incapacidades;
using Halsa.Backend.Application.Integraciones.Eventos;
using Halsa.Backend.Infrastructure.Worker;
using Halsa.Backend.Infrastructure.Worker.Jobs;
using Halsa.Backend.IntegrationTests.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Halsa.Backend.IntegrationTests.Integraciones.IngestaSql;
using static Halsa.Backend.IntegrationTests.Terminos.EventosSql;
using static Halsa.Backend.IntegrationTests.Terminos.PlazosSql;

namespace Halsa.Backend.IntegrationTests.Terminos;

/// <summary>
/// SPEC 09, paso 5: la apertura de plazos por eventos, con <c>halsa_app</c> y el actor de sistema. Cada test usa su
/// propio tenant: la validación asíncrona, que corre con el mismo evento, no ve duplicados de otro test. Los festivos
/// de 2026 se cargan antes, como lo haría <see cref="FestivosJob"/> al arrancar el Worker.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class AperturaPlazosTests(DatabaseFixture db) : IAsyncLifetime
{
    // Monday 5 Oct 2026, 10:00 in Bogotá. Two working days later is Wednesday 7, 23:59:59.
    private static readonly DateTimeOffset Lunes = new(2026, 10, 5, 10, 0, 0, Bogota);

    [Fact]
    public async Task FuratOfAMondayEvent_RadicatedOnTuesday_ShouldLeaveReporteFuratCumplido()
    {
        const string tenant = "ARL_AP_A_TIEMPO";
        (Guid caso, string radicado) = await InsertCasoAsync(db, tenant, Lunes, Lunes.AddDays(1));

        await PublicarAsync(db.Services, Radicado(caso, tenant, radicado));

        PlazoFila reporte = (await PlazosDelCasoAsync(db, caso)).Single(p => p.Regla == "REPORTE_FURAT");
        reporte.Estado.ShouldBe("CUMPLIDO");
        reporte.Extemporaneo.ShouldBeFalse();
        reporte.CumplidoEn.ShouldBe(Lunes.AddDays(1));
        reporte.CumplidoPor.ShouldBe("sistema");
        reporte.VenceEn.ShouldBe(new DateTimeOffset(2026, 10, 7, 23, 59, 59, Bogota));
        reporte.Radicado.ShouldBe(radicado);
        (await VencidosEmitidosAsync(reporte.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task FuratOfAMondayEvent_RadicatedOnThursday_ShouldBeVencidoAndExtemporaneo_WithItsEvent()
    {
        const string tenant = "ARL_AP_TARDE";
        (Guid caso, string radicado) = await InsertCasoAsync(db, tenant, Lunes, Lunes.AddDays(3));

        await PublicarAsync(db.Services, Radicado(caso, tenant, radicado));

        PlazoFila reporte = (await PlazosDelCasoAsync(db, caso)).Single(p => p.Regla == "REPORTE_FURAT");
        reporte.Estado.ShouldBe("VENCIDO");
        reporte.Extemporaneo.ShouldBeTrue();
        (await VencidosEmitidosAsync(reporte.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task At_ShouldOpenTheInvestigation_AndTheGraveRemissionOnlyWhenGrave()
    {
        (Guid grave, string radGrave) = await InsertCasoAsync(db, "ARL_AP_GRAVE", Lunes, Lunes.AddDays(1), esGrave: true);
        (Guid leve, string radLeve) = await InsertCasoAsync(db, "ARL_AP_LEVE", Lunes, Lunes.AddDays(1), esGrave: false);

        await PublicarAsync(db.Services, Radicado(grave, "ARL_AP_GRAVE", radGrave));
        await PublicarAsync(db.Services, Radicado(leve, "ARL_AP_LEVE", radLeve));

        (await PlazosDelCasoAsync(db, grave)).Select(p => p.Regla)
            .ShouldBe(["INVESTIGACION_AT", "REMISION_INVESTIGACION_GRAVE", "REPORTE_FURAT"]);
        (await PlazosDelCasoAsync(db, leve)).Select(p => p.Regla)
            .ShouldBe(["INVESTIGACION_AT", "REPORTE_FURAT"]);

        PlazoFila investigacion = (await PlazosDelCasoAsync(db, grave)).Single(p => p.Regla == "INVESTIGACION_AT");
        investigacion.Estado.ShouldBe("ABIERTO");
        investigacion.IniciaEn.ShouldBe(Lunes);
        investigacion.VenceEn.ShouldBe(new DateTimeOffset(2026, 10, 20, 23, 59, 59, Bogota));
        investigacion.AlertaEn.ShouldBe(new DateTimeOffset(2026, 10, 17, 23, 59, 59, Bogota));
    }

    [Fact]
    public async Task Furep_ShouldOpenOnlyReporteFurep()
    {
        const string tenant = "ARL_AP_FUREP";
        (Guid caso, string radicado) = await InsertCasoAsync(
            db, tenant, Lunes, Lunes.AddDays(1), tipoEvento: "EnfermedadLaboral");

        await PublicarAsync(db.Services, Radicado(caso, tenant, radicado, "FUREP"));

        (await PlazosDelCasoAsync(db, caso)).Select(p => p.Regla).ShouldBe(["REPORTE_FUREP"]);
    }

    [Fact]
    public async Task SameCasoRadicadoEvent_Twice_ShouldLeaveOnePlazoPerRule()
    {
        const string tenant = "ARL_AP_REPETIDO";
        (Guid caso, string radicado) = await InsertCasoAsync(db, tenant, Lunes, Lunes.AddDays(3), esGrave: true);
        CasoRadicadoIntegrationEvent evento = Radicado(caso, tenant, radicado);

        await PublicarAsync(db.Services, evento);
        await PublicarAsync(db.Services, evento);

        List<PlazoFila> plazos = await PlazosDelCasoAsync(db, caso);
        plazos.Count.ShouldBe(3);
        plazos.Select(p => p.Regla).Distinct().Count().ShouldBe(3);
        (await VencidosEmitidosAsync(plazos.Single(p => p.Regla == "REPORTE_FURAT").Id)).ShouldBe(1);
    }

    [Fact]
    public async Task RejectedCase_ShouldSuspendItsOpenPlazos()
    {
        const string tenant = "ARL_AP_RECHAZO";
        (Guid caso, string radicado) = await InsertCasoAsync(db, tenant, Lunes, Lunes.AddDays(1));
        await PublicarAsync(db.Services, Radicado(caso, tenant, radicado));

        await PublicarAsync(db.Services, new CasoRechazadoIntegrationEvent(caso, tenant, radicado, "N-1", "NO_AFILIADO", null));

        List<PlazoFila> plazos = await PlazosDelCasoAsync(db, caso);
        plazos.Single(p => p.Regla == "INVESTIGACION_AT").Estado.ShouldBe("SUSPENDIDO");
        plazos.Single(p => p.Regla == "REPORTE_FURAT").Estado.ShouldBe("CUMPLIDO");
    }

    [Fact]
    public async Task AlreadyRejectedCase_ShouldOpenItsPlazosSuspended()
    {
        const string tenant = "ARL_AP_YA_RECHAZADO";
        (Guid caso, string radicado) = await InsertCasoAsync(
            db, tenant, Lunes, Lunes.AddDays(1), trabajadorNumero: "1020304000", estado: "Rechazado");

        await PublicarAsync(db.Services, Radicado(caso, tenant, radicado));

        (await PlazosDelCasoAsync(db, caso)).Single(p => p.Regla == "INVESTIGACION_AT").Estado.ShouldBe("SUSPENDIDO");
    }

    [Fact]
    public async Task ChainWith120Days_ShouldExpireAt60DaysAfterFechaFin_AndAProrrogaOf30ShouldMoveIt()
    {
        const string tenant = "ARL_AP_TOPE";
        var inicio = new DateOnly(2026, 3, 2);
        (Guid caso, _) = await InsertCasoAsync(db, tenant, Lunes.AddMonths(-8), Lunes.AddMonths(-8));

        Guid inicial = await InsertIncapacidadAsync(db, tenant, caso, inicio, 30, 30);
        Guid anterior = inicial;
        for (int i = 1; i <= 3; i++)
        {
            anterior = await InsertIncapacidadAsync(db, tenant, caso, inicio.AddDays(30 * i), 30, 30 * (i + 1), inicial, anterior);
        }

        DateOnly fechaFin = inicio.AddDays(119);
        await PublicarAsync(db.Services, Incapacidad(anterior, caso, tenant, "PRORROGA", inicio.AddDays(90), fechaFin, 120));

        PlazoFila tope = (await PlazosDelCasoAsync(db, caso)).Single(p => p.Regla == "TOPE_IT_180");
        tope.ReferenciaId.ShouldBe(inicial);
        tope.IniciaEn.ShouldBe(new DateTimeOffset(2026, 3, 2, 0, 0, 0, Bogota));
        tope.VenceEn.ShouldBe(FinDelDia(fechaFin.AddDays(60)));
        tope.AlertaEn.ShouldBe(FinDelDia(fechaFin.AddDays(30)));
        tope.UltimaFechaFin.ShouldBe(fechaFin);

        // A 30-day extension: 150 days, so it expires 30 days after the new end.
        Guid prorroga = await InsertIncapacidadAsync(db, tenant, caso, fechaFin.AddDays(1), 30, 150, inicial, anterior);
        DateOnly nuevoFin = fechaFin.AddDays(30);
        await PublicarAsync(db.Services, Incapacidad(prorroga, caso, tenant, "PRORROGA", fechaFin.AddDays(1), nuevoFin, 150));

        tope = (await PlazosDelCasoAsync(db, caso)).Single(p => p.Regla == "TOPE_IT_180");
        tope.VenceEn.ShouldBe(FinDelDia(nuevoFin.AddDays(30)));
        tope.UltimaFechaFin.ShouldBe(nuevoFin);

        // An old redelivery does not move it back.
        await PublicarAsync(db.Services, Incapacidad(anterior, caso, tenant, "PRORROGA", inicio.AddDays(90), fechaFin, 120));
        (await PlazosDelCasoAsync(db, caso)).Single(p => p.Regla == "TOPE_IT_180").VenceEn.ShouldBe(FinDelDia(nuevoFin.AddDays(30)));
    }

    [Fact]
    public async Task NotifiedDictamen_ShouldOpenItsControversy_TenWorkingDaysLater()
    {
        const string tenant = "ARL_AP_DICTAMEN";
        (Guid caso, _) = await InsertCasoAsync(db, tenant, Lunes.AddMonths(-3), Lunes.AddMonths(-3));
        Guid dictamen = Guid.CreateVersion7();
        // Friday 2 Oct 2026: ten working days skip two weekends and Monday 12 (Día de la Raza).
        var notificado = new DateTimeOffset(2026, 10, 2, 11, 0, 0, Bogota);

        var evento = new DictamenPclNotificadoIntegrationEvent(
            dictamen, caso, tenant, notificado, "PERSONAL", new DateOnly(2026, 9, 30), 12.5m, "LABORAL");
        await PublicarAsync(db.Services, evento);
        await PublicarAsync(db.Services, evento);

        PlazoFila controversia = (await PlazosDelCasoAsync(db, caso)).Single();
        controversia.Regla.ShouldBe("CONTROVERSIA_DICTAMEN");
        controversia.ReferenciaId.ShouldBe(dictamen);
        controversia.VenceEn.ShouldBe(new DateTimeOffset(2026, 10, 19, 23, 59, 59, Bogota));
        controversia.AlertaEn.ShouldBe(new DateTimeOffset(2026, 10, 14, 23, 59, 59, Bogota));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public Task InitializeAsync() => new FestivosJob(
            db.Services.GetRequiredService<BaseDatosWorker>(),
            new FakeTimeProvider(Lunes),
            NullLogger<FestivosJob>.Instance)
        .AsegurarAsync(CancellationToken.None);

    public Task DisposeAsync() => Task.CompletedTask;

    private static CasoRadicadoIntegrationEvent Radicado(Guid caso, string tenant, string radicado, string tipo = "FURAT") =>
        new(caso, tenant, radicado, tipo, $"N-{caso:N}"[..20], Guid.NewGuid());

    private static IncapacidadRegistradaIntegrationEvent Incapacidad(
        Guid id, Guid caso, string tenant, string tipo, DateOnly inicio, DateOnly fin, int acumulados) =>
        new(id, caso, tenant, tipo, inicio, fin, fin.DayNumber - inicio.DayNumber + 1, acumulados, []);

    private static DateTimeOffset FinDelDia(DateOnly fecha) => new(fecha.ToDateTime(new TimeOnly(23, 59, 59)), Bogota);

    private Task<long> VencidosEmitidosAsync(Guid plazoId) => ContarAsync(db, """
        SELECT count(*) FROM outbox.outbox_messages
        WHERE type LIKE '%PlazoVencidoIntegrationEvent' AND (content->>'plazoId')::uuid = @plazo
        """, ("plazo", plazoId));
}
