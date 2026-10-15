using System.Buffers.Text;
using Halsa.Backend.Application.Webhooks;
using Halsa.Backend.Domain.Webhooks;
using Halsa.Backend.Infrastructure.Webhooks;
using Halsa.Backend.Infrastructure.Worker;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;
using static Halsa.Backend.IntegrationTests.Webhooks.WebhooksSql;

namespace Halsa.Backend.IntegrationTests.Webhooks;

/// <summary>
/// SPEC 10, paso 7: <see cref="WebhookDispatcherJob"/> como <c>halsa_worker</c> contra un receptor Kestrel, con un reloj
/// falso. El dispatcher es global: cada test siembra sus entregas en 2020 y corre el reloj ahí, así nunca toma las que
/// otros tests dejan a la hora real, y al terminar da de baja su suscripción.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class WebhookDispatcherTests(DatabaseFixture db)
{
    private static readonly DateTimeOffset Inicio = new(2020, 3, 10, 15, 0, 0, TimeSpan.Zero);

    private const string Payload = """{"tipo": "plazo.vencido", "eventoId": "0199e3a0-0000-7000-8000-000000000001"}""";

    [Fact]
    public async Task ThreeFailuresThenASuccess_ShouldEndEntregada_WithFourAttempts_AndTheSameEventId()
    {
        await using ReceptorPrueba receptor = await ReceptorPrueba.IniciarAsync();
        receptor.Responder(500, 500, 500, 200);
        (Guid suscripcion, byte[] secreto) = await NuevaSuscripcionAsync(receptor.Url());
        (Guid entrega, Guid evento) = await EntregaAsync(suscripcion, Inicio);

        DateTimeOffset ahora = Inicio;
        for (int i = 0; i < 4; i++)
        {
            await Job(ahora).ProcesarLoteAsync(CancellationToken.None);
            // Past the longest backoff of the first attempts (4 min, +20 %).
            ahora = ahora.AddMinutes(10);
        }

        EntregaFila fila = (await EntregasAsync(db, suscripcion)).Single(e => e.Id == entrega);
        fila.Estado.ShouldBe("ENTREGADA");
        fila.Intentos.ShouldBe(4);
        fila.UltimoStatus.ShouldBe(200);
        fila.UltimoError.ShouldBeNull();
        fila.EntregadaEn.ShouldNotBeNull();
        (await SuscripcionAsync(db, suscripcion)).Fallos.ShouldBe(0);

        ReceptorPrueba.Recibido[] recibidos = [.. receptor.Recibidos];
        recibidos.Length.ShouldBe(4);
        recibidos.Select(r => r.Cabecera("Halsa-Event-Id")).Distinct().ShouldBe([evento.ToString()]);
        recibidos.Select(r => r.Cabecera("Halsa-Delivery-Attempt")).ShouldBe(["1", "2", "3", "4"]);
        recibidos.ShouldAllBe(r => r.Cabecera("Halsa-Event-Type") == "plazo.vencido");
        recibidos.ShouldAllBe(r => r.Cabecera("Content-Type").StartsWith("application/json", StringComparison.Ordinal));
        recibidos.ShouldAllBe(r => r.FirmaValida(secreto, 0));
        recibidos.ShouldAllBe(r => r.Firmas.Count == 1);

        await BajaAsync(suscripcion);
    }

    [Fact]
    public async Task AFailure_ShouldScheduleTheRetryWithBackoff_AndCountItOnTheSubscription()
    {
        await using ReceptorPrueba receptor = await ReceptorPrueba.IniciarAsync();
        receptor.StatusPorDefecto = 503;
        (Guid suscripcion, _) = await NuevaSuscripcionAsync(receptor.Url());
        (Guid entrega, _) = await EntregaAsync(suscripcion, Inicio);

        await Job(Inicio).ProcesarLoteAsync(CancellationToken.None);

        EntregaFila fila = (await EntregasAsync(db, suscripcion)).Single(e => e.Id == entrega);
        fila.Estado.ShouldBe("PENDIENTE");
        fila.Intentos.ShouldBe(1);
        fila.UltimoStatus.ShouldBe(503);
        fila.UltimoError.ShouldBe("HTTP 503");
        // min(2^1 × 30 s, 4 h) ± 20 %.
        (fila.ProximoIntentoEn - Inicio).ShouldBeInRange(TimeSpan.FromSeconds(48), TimeSpan.FromSeconds(72));
        (await SuscripcionAsync(db, suscripcion)).Fallos.ShouldBe(1);

        await BajaAsync(suscripcion);
    }

    [Fact]
    public async Task TwentyFiveHoursAfterTheFirstFailedAttempt_ShouldBeDeadLetter()
    {
        await using ReceptorPrueba receptor = await ReceptorPrueba.IniciarAsync();
        receptor.StatusPorDefecto = 500;
        (Guid suscripcion, _) = await NuevaSuscripcionAsync(receptor.Url());
        (Guid entrega, _) = await EntregaAsync(suscripcion, Inicio);

        await Job(Inicio).ProcesarLoteAsync(CancellationToken.None);
        (await EntregasAsync(db, suscripcion)).Single(e => e.Id == entrega).Estado.ShouldBe("PENDIENTE");

        await Job(Inicio.AddHours(25)).ProcesarLoteAsync(CancellationToken.None);

        EntregaFila fila = (await EntregasAsync(db, suscripcion)).Single(e => e.Id == entrega);
        fila.Estado.ShouldBe("DEAD_LETTER");
        fila.Intentos.ShouldBe(2);

        // Never sent again.
        await Job(Inicio.AddHours(30)).ProcesarLoteAsync(CancellationToken.None);
        receptor.Recibidos.Count.ShouldBe(2);

        await BajaAsync(suscripcion);
    }

    [Fact]
    public async Task ReintentarADeadLetter_ShouldDeliverItInTheNextCycle()
    {
        await using ReceptorPrueba receptor = await ReceptorPrueba.IniciarAsync();
        string tenant = TenantNuevo("WH_REINTENTO");
        (Guid suscripcion, _) = await NuevaSuscripcionAsync(receptor.Url(), tenant);
        (Guid entrega, _) = await EntregaAsync(suscripcion, Inicio, estado: "DEAD_LETTER");

        Result reintento = await EnviarAsync(Integracion(tenant), new ReintentarEntregaWebhookCommand(suscripcion, entrega));
        reintento.IsSuccess.ShouldBeTrue(reintento.IsFailure ? reintento.Error.ToString() : null);

        // The retry starts a new 24 h window now, in real time: the next cycle runs then.
        EntregaFila reencolada = (await EntregasAsync(db, suscripcion)).Single(e => e.Id == entrega);
        reencolada.Estado.ShouldBe("PENDIENTE");
        reencolada.Intentos.ShouldBe(0);
        await AlInicioAsync(suscripcion);

        await Job(Inicio).ProcesarLoteAsync(CancellationToken.None);

        (await EntregasAsync(db, suscripcion)).Single(e => e.Id == entrega).Estado.ShouldBe("ENTREGADA");
        receptor.Recibidos.ShouldHaveSingleItem();

        await BajaAsync(suscripcion);
    }

    [Fact]
    public async Task FiftyFailuresInARow_ShouldSuspendTheSubscription_AndPatchActivaShouldReactivateIt()
    {
        await using ReceptorPrueba receptor = await ReceptorPrueba.IniciarAsync();
        receptor.StatusPorDefecto = 500;
        string tenant = TenantNuevo("WH_SUSPENDER");
        (Guid suscripcion, _) = await NuevaSuscripcionAsync(receptor.Url(), tenant);
        for (int i = 0; i < 50; i++)
        {
            await EntregaAsync(suscripcion, Inicio);
        }

        await Job(Inicio).ProcesarLoteAsync(CancellationToken.None);

        (string estado, int fallos) = await SuscripcionAsync(db, suscripcion);
        estado.ShouldBe("SUSPENDIDA");
        fallos.ShouldBe(50);

        // A suspended subscription is not sent to.
        await Job(Inicio.AddHours(5)).ProcesarLoteAsync(CancellationToken.None);
        receptor.Recibidos.Count.ShouldBe(50);

        Result<SuscripcionWebhookResponse> patch = await SendAsync(db, Integracion(tenant), new CambiarSuscripcionWebhookCommand(
            suscripcion, new CambiarSuscripcionWebhookRequest(null, null, "ACTIVA")));
        patch.IsSuccess.ShouldBeTrue(patch.IsFailure ? patch.Error.ToString() : null);
        (await SuscripcionAsync(db, suscripcion)).ShouldBe(("ACTIVA", 0));

        await BajaAsync(suscripcion);
    }

    [Fact]
    public async Task A410_ShouldDiscardTheDelivery_AndSuspendTheSubscription()
    {
        await using ReceptorPrueba receptor = await ReceptorPrueba.IniciarAsync();
        receptor.Responder(410);
        (Guid suscripcion, _) = await NuevaSuscripcionAsync(receptor.Url());
        (Guid entrega, _) = await EntregaAsync(suscripcion, Inicio);

        await Job(Inicio).ProcesarLoteAsync(CancellationToken.None);

        EntregaFila fila = (await EntregasAsync(db, suscripcion)).Single(e => e.Id == entrega);
        fila.Estado.ShouldBe("DESCARTADA");
        fila.UltimoStatus.ShouldBe(410);
        (await SuscripcionAsync(db, suscripcion)).Estado.ShouldBe("SUSPENDIDA");

        await BajaAsync(suscripcion);
    }

    [Fact]
    public async Task A302_ShouldNotBeFollowed_AndCountAsAFailure()
    {
        await using ReceptorPrueba receptor = await ReceptorPrueba.IniciarAsync();
        receptor.Responder(302);
        (Guid suscripcion, _) = await NuevaSuscripcionAsync(receptor.Url());
        (Guid entrega, _) = await EntregaAsync(suscripcion, Inicio);

        await Job(Inicio).ProcesarLoteAsync(CancellationToken.None);

        receptor.Recibidos.ShouldHaveSingleItem().Ruta.ShouldBe("/hook");
        EntregaFila fila = (await EntregasAsync(db, suscripcion)).Single(e => e.Id == entrega);
        fila.Estado.ShouldBe("PENDIENTE");
        fila.UltimoStatus.ShouldBe(302);
        (await SuscripcionAsync(db, suscripcion)).Fallos.ShouldBe(1);

        await BajaAsync(suscripcion);
    }

    [Fact]
    public async Task ANameThatResolvesToLoopback_ShouldBeBlockedAtConnection_WithoutPermitirDestinosLocales()
    {
        await using ReceptorPrueba receptor = await ReceptorPrueba.IniciarAsync();
        // As if a public name had passed the registration and then rebound to 127.0.0.1.
        string url = receptor.Url().Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        (Guid suscripcion, _) = await NuevaSuscripcionAsync(url);
        (Guid entrega, _) = await EntregaAsync(suscripcion, Inicio);

        await Job(Inicio, permitirLocales: false).ProcesarLoteAsync(CancellationToken.None);

        receptor.Recibidos.ShouldBeEmpty();
        EntregaFila fila = (await EntregasAsync(db, suscripcion)).Single(e => e.Id == entrega);
        fila.Estado.ShouldBe("PENDIENTE");
        fila.UltimoStatus.ShouldBeNull();
        fila.UltimoError.ShouldNotBeNull().ShouldContain("bloqueada");

        await BajaAsync(suscripcion);
    }

    [Fact]
    public async Task AfterARotation_SendsShouldCarryTwoSignaturesFor24Hours_AndThenOne()
    {
        await using ReceptorPrueba receptor = await ReceptorPrueba.IniciarAsync();
        string tenant = TenantNuevo("WH_ROTACION");
        (Guid suscripcion, byte[] anterior) = await NuevaSuscripcionAsync(receptor.Url(), tenant);

        Result<SecretoRotadoResponse> rotado = await SendAsync(db, Integracion(tenant), new RotarSecretoWebhookCommand(suscripcion));
        rotado.IsSuccess.ShouldBeTrue(rotado.IsFailure ? rotado.Error.ToString() : null);
        byte[] nuevo = Base64Url.DecodeFromChars(rotado.Value.Secreto);
        rotado.Value.SecretoAnteriorExpiraEn.ShouldBe(DateTimeOffset.UtcNow.AddHours(24), TimeSpan.FromMinutes(1));

        // As if the rotation had run at Inicio, where this class's clock runs.
        DateTimeOffset expira = Inicio.AddHours(24);
        await EjecutarAsync(db, "UPDATE integracion.suscripciones_webhook SET secreto_anterior_expira_en = @e WHERE id = @s",
            ("e", expira), ("s", suscripcion));

        await EntregaAsync(suscripcion, expira.AddHours(-1));
        await Job(expira.AddHours(-1)).ProcesarLoteAsync(CancellationToken.None);
        await EntregaAsync(suscripcion, expira.AddMinutes(1));
        await Job(expira.AddMinutes(1)).ProcesarLoteAsync(CancellationToken.None);

        ReceptorPrueba.Recibido[] recibidos = [.. receptor.Recibidos];
        recibidos.Length.ShouldBe(2);

        recibidos[0].Firmas.Count.ShouldBe(2);
        recibidos[0].FirmaValida(nuevo, 0).ShouldBeTrue();
        recibidos[0].FirmaValida(anterior, 1).ShouldBeTrue();

        recibidos[1].Firmas.Count.ShouldBe(1);
        recibidos[1].FirmaValida(nuevo, 0).ShouldBeTrue();

        await BajaAsync(suscripcion);
    }

    [Fact]
    public async Task TwoWorkersInParallel_ShouldSendEachDeliveryOncePerAttempt()
    {
        await using ReceptorPrueba receptor = await ReceptorPrueba.IniciarAsync();
        (Guid suscripcion, _) = await NuevaSuscripcionAsync(receptor.Url());
        var eventos = new List<Guid>();
        for (int i = 0; i < 60; i++)
        {
            eventos.Add((await EntregaAsync(suscripcion, Inicio)).Evento);
        }

        // Small batches, so both workers keep claiming while the other sends.
        await Task.WhenAll(
            Task.Run(() => DrenarAsync(Job(Inicio, lote: 7))),
            Task.Run(() => DrenarAsync(Job(Inicio, lote: 7))));

        receptor.Recibidos.Select(r => r.Cabecera("Halsa-Event-Id")).Order(StringComparer.Ordinal)
            .ShouldBe(eventos.Select(e => e.ToString()).Order(StringComparer.Ordinal));
        (await EntregasAsync(db, suscripcion)).ShouldAllBe(e => e.Estado == "ENTREGADA" && e.Intentos == 1);

        await BajaAsync(suscripcion);
    }

    [Fact]
    public async Task PurgaEntregas_ShouldDeleteOnlyTheDeliveredOlderThan90Days()
    {
        (Guid suscripcion, _) = await NuevaSuscripcionAsync("https://receptor.example.com/purga");
        DateTimeOffset ahora = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
        (Guid vieja, _) = await EntregaAsync(suscripcion, ahora.AddDays(-91), estado: "ENTREGADA");
        (Guid reciente, _) = await EntregaAsync(suscripcion, ahora.AddDays(-89), estado: "ENTREGADA");
        (Guid deadLetter, _) = await EntregaAsync(suscripcion, ahora.AddDays(-200), estado: "DEAD_LETTER");

        int borradas = await new PurgaEntregasWebhookJob(
            db.Services.GetRequiredService<BaseDatosWorker>(), new FakeTimeProvider(ahora),
            NullLogger<PurgaEntregasWebhookJob>.Instance).PurgarAsync(CancellationToken.None);

        borradas.ShouldBeGreaterThanOrEqualTo(1);
        List<Guid> quedan = [.. (await EntregasAsync(db, suscripcion)).Select(e => e.Id)];
        quedan.ShouldNotContain(vieja);
        quedan.ShouldContain(reciente);
        quedan.ShouldContain(deadLetter);

        await BajaAsync(suscripcion);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private WebhookDispatcherJob Job(DateTimeOffset ahora, bool permitirLocales = true, int lote = 50)
    {
        IOptions<WebhooksOptions> options = Options.Create(new WebhooksOptions
        {
            PermitirDestinosLocales = permitirLocales,
            Lote = lote,
            MaxParalelo = 4,
            Timeout = TimeSpan.FromSeconds(5)
        });

        return new WebhookDispatcherJob(
            db.Services.GetRequiredService<BaseDatosWorker>(),
            new SafeWebhookHttpClient(options),
            db.Services.GetRequiredService<WebhookSecretProtector>(),
            options,
            new FakeTimeProvider(ahora),
            NullLogger<WebhookDispatcherJob>.Instance);
    }

    private static async Task DrenarAsync(WebhookDispatcherJob job)
    {
        while ((await job.ProcesarLoteAsync(CancellationToken.None)).Total > 0)
        {
        }
    }

    private async Task<Result> EnviarAsync(FakeTenantContext actor, IRequest<Result> request)
    {
        await using AsyncServiceScope scope = db.CreateScope(actor);
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private static FakeTenantContext Integracion(string tenant) => new(ActorTipo.Integracion, new TenantId(tenant), null);

    /// <summary>An active subscription with a real secret wrapped with the fixture's KEK. Returns the secret in clear.</summary>
    private async Task<(Guid Id, byte[] Secreto)> NuevaSuscripcionAsync(string url, string? tenant = null)
    {
        var id = SuscripcionWebhookId.New();
        SecretoNuevo secreto = db.Services.GetRequiredService<WebhookSecretProtector>().Generar(id);

        await InsertSuscripcionAsync(db, $"tenant:{tenant ?? TenantNuevo("WH_DISP")}", ["plazo.vencido"], url: url,
            secretoCt: secreto.SecretoCt, secretoKeyId: secreto.KeyId, id: id.Value);

        return (id.Value, Base64Url.DecodeFromChars(secreto.Secreto));
    }

    /// <summary>A delivery as the fan-out writes it, due at <paramref name="desde"/>.</summary>
    private async Task<(Guid Id, Guid Evento)> EntregaAsync(Guid suscripcion, DateTimeOffset desde, string estado = "PENDIENTE")
    {
        Guid id = Guid.CreateVersion7();
        Guid evento = Guid.CreateVersion7();

        await EjecutarAsync(db, """
            INSERT INTO integracion.entregas_webhook
              (id, suscripcion_id, evento_id, tipo, payload, estado, intentos, primer_intento_en, proximo_intento_en,
               entregada_en)
            VALUES (@id, @s, @evento, 'plazo.vencido', CAST(@payload AS jsonb), @estado, 0, @desde, @desde,
                    CASE WHEN @estado = 'ENTREGADA' THEN @desde END)
            """, ("id", id), ("s", suscripcion), ("evento", evento), ("payload", Payload), ("estado", estado),
            ("desde", desde.ToUniversalTime()));

        return (id, evento);
    }

    /// <summary>Moves the subscription's pending deliveries to <see cref="Inicio"/>, where this class's clock runs.</summary>
    private Task AlInicioAsync(Guid suscripcion) => EjecutarAsync(db, """
        UPDATE integracion.entregas_webhook SET primer_intento_en = @inicio, proximo_intento_en = @inicio
        WHERE suscripcion_id = @s AND estado = 'PENDIENTE'
        """, ("s", suscripcion), ("inicio", Inicio));

    private Task BajaAsync(Guid suscripcion) => WebhooksSql.BajaAsync(db, suscripcion);
}
