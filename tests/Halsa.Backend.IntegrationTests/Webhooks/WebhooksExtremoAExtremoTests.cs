using System.Buffers.Text;
using System.Text.Json.Nodes;
using Halsa.Backend.Application.Casos.Commands.Remitir;
using Halsa.Backend.Application.Webhooks;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Webhooks;
using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.Infrastructure.Webhooks;
using Halsa.Backend.Infrastructure.Worker;
using Halsa.Backend.Infrastructure.Worker.Jobs;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;
using static Halsa.Backend.IntegrationTests.Terminos.PlazosSql;
using static Halsa.Backend.IntegrationTests.Webhooks.WebhooksSql;

namespace Halsa.Backend.IntegrationTests.Webhooks;

/// <summary>
/// SPEC 10, criterios de aceptación de punta a punta: del hecho (un plazo que vence, un caso que se remite) al outbox, del
/// outbox al fan-out por el bus del Worker, y del dispatcher al receptor. Las entregas se corren a 2020 antes de enviarlas,
/// como en <see cref="WebhookDispatcherTests"/>, para no tomar las que otros tests dejan a la hora real.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class WebhooksExtremoAExtremoTests(DatabaseFixture db)
{
    private static readonly DateTimeOffset Envio = new(2020, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Prohibidas = ["diagnosticos", "fundamentos", "contenido", "descripcion"];

    [Fact]
    public async Task WhenAPlazoExpires_TheReceiverOfArlA_ShouldGetASignedPost_AndTheReceiverOfArlB_Nothing()
    {
        await using ReceptorPrueba receptorA = await ReceptorPrueba.IniciarAsync();
        await using ReceptorPrueba receptorB = await ReceptorPrueba.IniciarAsync();
        string arlA = TenantNuevo("WH_E2E_A");
        string arlB = TenantNuevo("WH_E2E_B");
        (Guid suscripcionA, byte[] secretoA) = await SuscripcionAsync($"tenant:{arlA}", "plazo.vencido", receptorA.Url());
        (Guid suscripcionB, _) = await SuscripcionAsync($"tenant:{arlB}", "plazo.vencido", receptorB.Url());

        // A plazo of ARL_A that expired long ago: the sweep turns it VENCIDO and enqueues its event.
        DateTimeOffset vence = new(2019, 6, 1, 12, 0, 0, TimeSpan.Zero);
        (Guid caso, string radicado) = await InsertCasoAsync(db, arlA, vence.AddDays(-20), vence.AddDays(-20));
        Guid plazo = await InsertPlazoAsync(db, arlA, caso, "INVESTIGACION_AT", vence, vence.AddDays(-3), radicado: radicado);
        await new BarridoPlazosJob(
                db.Services.GetRequiredService<BaseDatosWorker>(), new FakeTimeProvider(vence.AddHours(1)),
                NullLogger<BarridoPlazosJob>.Instance)
            .BarrerAsync(CancellationToken.None);

        await EntregarDelOutboxAsync("PlazoVencidoIntegrationEvent", "plazoId", plazo);
        await EnviarAsync(suscripcionA, suscripcionB);

        ReceptorPrueba.Recibido recibido = receptorA.Recibidos.ShouldHaveSingleItem();
        recibido.FirmaValida(secretoA).ShouldBeTrue();
        recibido.Cabecera("Halsa-Event-Type").ShouldBe("plazo.vencido");
        JsonObject cuerpo = JsonNode.Parse(recibido.Cuerpo)!.AsObject();
        cuerpo["casoId"]!.GetValue<Guid>().ShouldBe(caso);
        cuerpo["radicado"]!.GetValue<string>().ShouldBe(radicado);
        cuerpo["plazo"]!["regla"]!.GetValue<string>().ShouldBe("INVESTIGACION_AT");
        cuerpo["eventoId"]!.GetValue<Guid>().ToString().ShouldBe(recibido.Cabecera("Halsa-Event-Id"));

        receptorB.Recibidos.ShouldBeEmpty();
        (await EntregasAsync(db, suscripcionB)).ShouldBeEmpty();

        await BajaAsync(db, suscripcionA, suscripcionB);
    }

    [Fact]
    public async Task RemittingACase_TheSubscriptionOfThatIps_ShouldGetCasoRemitido_WithTheWorkerAndNothingClinical()
    {
        await using ReceptorPrueba receptor = await ReceptorPrueba.IniciarAsync();
        const string ips = "110011234501";
        string tenant = TenantNuevo("WH_E2E_REM");
        (Guid suscripcion, byte[] secreto) = await SuscripcionAsync($"ips:{ips}", "caso.remitido", receptor.Url());
        (Guid caso, string radicado) = await InsertCasoAsync(
            db, tenant, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-3), trabajadorNumero: "1020304050");

        Result<Guid> remision = await SendAsync(db, FakeTenantContext.Arl(tenant), new RemitirCasoCommand(new CasoMedicoId(caso), ips));
        remision.IsSuccess.ShouldBeTrue(remision.IsFailure ? remision.Error.ToString() : null);

        await EntregarDelOutboxAsync("CasoRemitidoIntegrationEvent", "casoId", caso);
        await EnviarAsync(suscripcion);

        ReceptorPrueba.Recibido recibido = receptor.Recibidos.ShouldHaveSingleItem();
        recibido.FirmaValida(secreto).ShouldBeTrue();
        JsonObject cuerpo = JsonNode.Parse(recibido.Cuerpo)!.AsObject();
        cuerpo["tipo"]!.GetValue<string>().ShouldBe("caso.remitido");
        cuerpo["casoId"]!.GetValue<Guid>().ShouldBe(caso);
        cuerpo["radicado"]!.GetValue<string>().ShouldBe(radicado);
        cuerpo["remisionId"]!.GetValue<Guid>().ShouldBe(remision.Value);
        cuerpo["tipoEvento"]!.GetValue<string>().ShouldBe("AccidenteTrabajo");
        cuerpo["trabajador"]!["tipo"]!.GetValue<string>().ShouldBe("CC");
        cuerpo["trabajador"]!["numero"]!.GetValue<string>().ShouldBe("1020304050");
        recibido.Cuerpo.ShouldNotContain("diagnostic", Case.Insensitive);
        Prohibidas.ShouldAllBe(p => !recibido.Cuerpo.Contains($"\"{p}\"", StringComparison.OrdinalIgnoreCase));

        await BajaAsync(db, suscripcion);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(Guid Id, byte[] Secreto)> SuscripcionAsync(string emisor, string evento, string url)
    {
        var id = SuscripcionWebhookId.New();
        SecretoNuevo secreto = db.Services.GetRequiredService<WebhookSecretProtector>().Generar(id);

        await InsertSuscripcionAsync(db, emisor, [evento], url: url, secretoCt: secreto.SecretoCt,
            secretoKeyId: secreto.KeyId, id: id.Value);

        return (id.Value, Base64Url.DecodeFromChars(secreto.Secreto));
    }

    /// <summary>
    /// Hands the outbox message of the test's fact to the in-process bus, exactly as <c>OutboxProcessor</c> would; draining
    /// the whole shared outbox would also deliver the other tests' events.
    /// </summary>
    private async Task EntregarDelOutboxAsync(string tipo, string campo, Guid valor)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.SuperuserConnectionString);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT type, content::text FROM outbox.outbox_messages
            WHERE type LIKE '%' || @tipo AND (content->>'{campo}')::uuid = @valor
            """;
        cmd.Parameters.AddWithValue("tipo", tipo);
        cmd.Parameters.AddWithValue("valor", valor);

        var mensajes = new List<(string Type, string Content)>();
        await using (NpgsqlDataReader r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                mensajes.Add((r.GetString(0), r.GetString(1)));
            }
        }

        (string type, string content) = mensajes.ShouldHaveSingleItem();
        await db.Services.GetRequiredService<IEventBus>().PublishAsync(type, content);
    }

    /// <summary>Moves the subscriptions' pending deliveries to <see cref="Envio"/> and runs one dispatcher pass there.</summary>
    private async Task EnviarAsync(params Guid[] suscripciones)
    {
        await EjecutarAsync(db, """
            UPDATE integracion.entregas_webhook SET primer_intento_en = @envio, proximo_intento_en = @envio
            WHERE suscripcion_id = ANY(@s) AND estado = 'PENDIENTE'
            """, ("envio", Envio), ("s", suscripciones));

        IOptions<WebhooksOptions> options = Options.Create(new WebhooksOptions { PermitirDestinosLocales = true });
        await new WebhookDispatcherJob(
                db.Services.GetRequiredService<BaseDatosWorker>(),
                new SafeWebhookHttpClient(options),
                db.Services.GetRequiredService<WebhookSecretProtector>(),
                options,
                new FakeTimeProvider(Envio),
                NullLogger<WebhookDispatcherJob>.Instance)
            .ProcesarLoteAsync(CancellationToken.None);
    }
}
