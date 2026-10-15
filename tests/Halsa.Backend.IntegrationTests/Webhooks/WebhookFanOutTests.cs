using System.Text.Json.Nodes;
using Halsa.Backend.Application.Casos;
using Halsa.Backend.Application.Incapacidades;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Application.Plazos;
using Halsa.Backend.Domain.Webhooks;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.IntegrationTests.Abstractions;
using Microsoft.EntityFrameworkCore;
using static Halsa.Backend.IntegrationTests.Terminos.EventosSql;
using static Halsa.Backend.IntegrationTests.Terminos.PlazosSql;
using static Halsa.Backend.IntegrationTests.Webhooks.WebhooksSql;

namespace Halsa.Backend.IntegrationTests.Webhooks;

/// <summary>
/// SPEC 10, paso 6: <c>WebhookFanOutHandler</c> a través del bus del outbox, como lo entrega el Worker, y la RLS de las
/// tablas nuevas.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class WebhookFanOutTests(DatabaseFixture db)
{
    private static readonly PlazoResumen Plazo = new("INVESTIGACION_AT", "Res. 1401/2007", DateTimeOffset.UtcNow.AddDays(3));

    [Fact]
    public async Task AnEventWithTwoSubscriptions_ShouldProduceTwoDeliveries_AndARepeatedEventNoMore()
    {
        string tenant = TenantNuevo("WH_FAN");
        Guid primera = await InsertSuscripcionAsync(db, $"tenant:{tenant}", ["plazo.vencido"]);
        Guid segunda = await InsertSuscripcionAsync(db, $"tenant:{tenant}", ["plazo.vencido", "caso.validado"]);
        var evento = new PlazoVencidoIntegrationEvent(Guid.NewGuid(), tenant, Guid.NewGuid(), "ATEL-FAN", null, Plazo);

        await PublicarAsync(db.Services, evento);
        await PublicarAsync(db.Services, evento);

        EntregaFila entrega = (await EntregasAsync(db, primera)).ShouldHaveSingleItem();
        (await EntregasAsync(db, segunda)).ShouldHaveSingleItem().EventoId.ShouldBe(evento.EventId);

        entrega.EventoId.ShouldBe(evento.EventId);
        entrega.Tipo.ShouldBe("plazo.vencido");
        entrega.Estado.ShouldBe("PENDIENTE");
        entrega.Intentos.ShouldBe(0);
        entrega.ProximoIntentoEn.ShouldBe(entrega.PrimerIntentoEn);

        JsonObject payload = JsonNode.Parse(entrega.Payload)!.AsObject();
        payload["eventoId"]!.GetValue<Guid>().ShouldBe(evento.EventId);
        payload["radicado"]!.GetValue<string>().ShouldBe("ATEL-FAN");
        payload["plazo"]!["regla"]!.GetValue<string>().ShouldBe("INVESTIGACION_AT");

        await BajaAsync(db, primera, segunda);
    }

    [Fact]
    public async Task OnlyActiveSubscriptions_OfTheRecipient_ThatIncludeTheType_ShouldGetADelivery()
    {
        string tenant = TenantNuevo("WH_FILTRO");
        string otro = TenantNuevo("WH_FILTRO_OTRO");
        Guid activa = await InsertSuscripcionAsync(db, $"tenant:{tenant}", ["plazo.vencido"]);
        Guid pausada = await InsertSuscripcionAsync(db, $"tenant:{tenant}", ["plazo.vencido"], estado: "PAUSADA");
        Guid suspendida = await InsertSuscripcionAsync(db, $"tenant:{tenant}", ["plazo.vencido"], estado: "SUSPENDIDA");
        Guid otroTipo = await InsertSuscripcionAsync(db, $"tenant:{tenant}", ["plazo.proximo-a-vencer"]);
        Guid otroTenant = await InsertSuscripcionAsync(db, $"tenant:{otro}", ["plazo.vencido"]);

        await PublicarAsync(db.Services, new PlazoVencidoIntegrationEvent(Guid.NewGuid(), tenant, Guid.NewGuid(), "ATEL-X", null, Plazo));

        (await EntregasAsync(db, activa)).Count.ShouldBe(1);
        (await EntregasAsync(db, pausada)).ShouldBeEmpty();
        (await EntregasAsync(db, suspendida)).ShouldBeEmpty();
        (await EntregasAsync(db, otroTipo)).ShouldBeEmpty();
        (await EntregasAsync(db, otroTenant)).ShouldBeEmpty();

        await BajaAsync(db, activa, pausada, suspendida, otroTipo, otroTenant);
    }

    [Fact]
    public async Task AnEventWithoutRadicado_ShouldReadItFromTheCase()
    {
        string tenant = TenantNuevo("WH_RADICADO");
        (Guid caso, string radicado) = await InsertCasoAsync(db, tenant, DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddDays(-5));
        Guid suscripcion = await InsertSuscripcionAsync(db, $"tenant:{tenant}", ["incapacidad.registrada"]);
        // The plazos handler of SPEC 09 also hears the event, and needs the row.
        Guid incapacidad = await InsertIncapacidadAsync(db, tenant, caso, new DateOnly(2026, 10, 1), dias: 5, diasAcumulados: 5);

        await PublicarAsync(db.Services, new IncapacidadRegistradaIntegrationEvent(
            incapacidad, caso, tenant, "INICIAL", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5), 5, 5, []));

        JsonObject payload = JsonNode.Parse((await EntregasAsync(db, suscripcion)).ShouldHaveSingleItem().Payload)!.AsObject();
        payload["radicado"]!.GetValue<string>().ShouldBe(radicado);
        payload["tipoIncapacidad"]!.GetValue<string>().ShouldBe("INICIAL");

        await BajaAsync(db, suscripcion);
    }

    [Fact]
    public async Task CasoRemitido_ShouldReachTheSubscriptionOfThatIps_Only()
    {
        string ips = IpsNueva();
        Guid deLaIps = await InsertSuscripcionAsync(db, $"ips:{ips}", ["caso.remitido"]);
        Guid deOtraIps = await InsertSuscripcionAsync(db, $"ips:{IpsNueva()}", ["caso.remitido"]);

        await PublicarAsync(db.Services, new CasoRemitidoIntegrationEvent(
            Guid.NewGuid(), "ARL_A", "ATEL-REM", Guid.NewGuid(), ips, "AccidenteTrabajo", DateTimeOffset.UtcNow, "CC", "1020304050"));

        JsonObject payload = JsonNode.Parse((await EntregasAsync(db, deLaIps)).ShouldHaveSingleItem().Payload)!.AsObject();
        payload["trabajador"]!["numero"]!.GetValue<string>().ShouldBe("1020304050");
        (await EntregasAsync(db, deOtraIps)).ShouldBeEmpty();

        await BajaAsync(db, deLaIps, deOtraIps);
    }

    // ── RLS ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HalsaApp_ShouldOnlySeeTheSubscriptionsAndDeliveriesOfItsOwnEmisor()
    {
        string duena = TenantNuevo("WH_RLS");
        Guid suscripcion = await InsertSuscripcionAsync(db, $"tenant:{duena}", ["plazo.vencido"]);
        await PublicarAsync(db.Services, new PlazoVencidoIntegrationEvent(Guid.NewGuid(), duena, Guid.NewGuid(), "ATEL-RLS", null, Plazo));

        await using (ApplicationDbContext propia = db.CreateDbContext(FakeTenantContext.Arl(duena)))
        {
            (await propia.SuscripcionesWebhook.IgnoreQueryFilters().CountAsync(s => s.Id == new SuscripcionWebhookId(suscripcion))).ShouldBe(1);
            (await propia.EntregasWebhook.IgnoreQueryFilters().CountAsync(e => e.SuscripcionId == new SuscripcionWebhookId(suscripcion))).ShouldBe(1);
        }

        // Without the EF filter, only the RLS stands between another emisor and the rows.
        await using ApplicationDbContext ajena = db.CreateDbContext(FakeTenantContext.Arl(TenantNuevo("WH_RLS_OTRA")));
        (await ajena.SuscripcionesWebhook.IgnoreQueryFilters().CountAsync(s => s.Id == new SuscripcionWebhookId(suscripcion))).ShouldBe(0);
        (await ajena.EntregasWebhook.IgnoreQueryFilters().CountAsync(e => e.SuscripcionId == new SuscripcionWebhookId(suscripcion))).ShouldBe(0);

        await BajaAsync(db, suscripcion);
    }

    [Fact]
    public async Task HalsaApp_ShouldNotInsertADelivery()
    {
        string tenant = TenantNuevo("WH_INSERT");
        Guid suscripcion = await InsertSuscripcionAsync(db, $"tenant:{tenant}", ["plazo.vencido"]);

        await using ApplicationDbContext app = db.CreateDbContext(FakeTenantContext.Arl(tenant));

        Npgsql.PostgresException ex = await Should.ThrowAsync<Npgsql.PostgresException>(() => app.Database.ExecuteSqlAsync($"""
            INSERT INTO integracion.entregas_webhook
              (id, suscripcion_id, evento_id, tipo, payload, estado, intentos, primer_intento_en, proximo_intento_en)
            VALUES (gen_random_uuid(), {suscripcion}, gen_random_uuid(), 'plazo.vencido', '[]', 'PENDIENTE', 0, now(), now())
            """));
        ex.SqlState.ShouldBe(Npgsql.PostgresErrorCodes.InsufficientPrivilege);

        await BajaAsync(db, suscripcion);
    }
}
