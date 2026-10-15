using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using Halsa.Backend.FunctionalTests.Clinico;
using Halsa.Backend.FunctionalTests.Integraciones.Webhooks;
using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.Infrastructure.Worker;
using Halsa.Backend.Infrastructure.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;
using static Halsa.Backend.FunctionalTests.Pacientes.PacientesSeed;

namespace Halsa.Backend.FunctionalTests.Pacientes;

/// <summary>
/// SPEC 12, step 4: the HC copy requests. The patient creates them, the ARL's privacy officer works its inbox, and the
/// legal deadline <c>SOLICITUD_COPIA_HC</c> opens with the request (through the outbox) and is fulfilled when it closes.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class SolicitudesCopiaHcEndpointsTests(ApiFactory factory)
{
    private static string Ruta(Guid caso) => $"v1/paciente/casos/{caso}/solicitudes-copia-hc";

    private static object Cuerpo(string medio = "CORREO_CERTIFICADO", string? direccion = "Calle 1 # 2-3, Bogotá") =>
        new { medioEntrega = medio, direccionOCorreo = direccion, observaciones = "Para mi abogado." };

    [Fact]
    public async Task ThePatient_ShouldCreateOne_ASecondOneIs409_AndThePrivacyOfficerClosesIt_FulfillingTheDeadline()
    {
        string tenant = TenantNuevo("ARL_PAC_HC");
        Guid caso = await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento(), estado: "EnAtencion");
        HttpClient paciente = await VinculadoAsync(factory, tenant, caso);

        HttpResponseMessage creada = await paciente.PostAsJsonAsync(Ruta(caso), Cuerpo(), JsonOptions);
        creada.StatusCode.ShouldBe(HttpStatusCode.Created, await creada.Content.ReadAsStringAsync());
        JsonObject body = await BodyAsync(creada);
        Guid id = body["id"]!.GetValue<Guid>();
        body["estado"]!.GetValue<string>().ShouldBe("RECIBIDA");

        HttpResponseMessage segunda = await paciente.PostAsJsonAsync(Ruta(caso), Cuerpo("PRESENCIAL", null), JsonOptions);
        segunda.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(segunda)).ShouldBe("SolicitudCopiaHc.YaAbierta");

        await ProcesarOutboxAsync();
        (await EstadoPlazoAsync(id)).ShouldBe("ABIERTO");

        HttpClient privacidad = Privacidad(factory, tenant);
        JsonArray bandeja = (await privacidad.GetFromJsonAsync<JsonArray>("v1/solicitudes-copia-hc?estado=RECIBIDA", JsonOptions))!;
        JsonObject fila = bandeja.ShouldHaveSingleItem()!.AsObject();
        fila["id"]!.GetValue<Guid>().ShouldBe(id);
        fila["casoId"]!.GetValue<Guid>().ShouldBe(caso);
        fila["medioEntrega"]!.GetValue<string>().ShouldBe("CORREO_CERTIFICADO");

        (await privacidad.PostAsync($"v1/solicitudes-copia-hc/{id}/tramitar", content: null))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await privacidad.PostAsJsonAsync($"v1/solicitudes-copia-hc/{id}/cerrar",
                new { resultado = "ENTREGADA", nota = "Copia enviada por correo certificado, guía 123." }, JsonOptions))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await EstadoPlazoAsync(id)).ShouldBe("CUMPLIDO");

        JsonObject mia = (await paciente.GetFromJsonAsync<JsonArray>("v1/paciente/solicitudes-copia-hc", JsonOptions))!
            .ShouldHaveSingleItem()!.AsObject();
        mia["estado"]!.GetValue<string>().ShouldBe("ENTREGADA");

        (await AuditoriaAsync(factory, caso)).Where(f => f.Recurso == "SOLICITUD_COPIA_HC")
            .Select(f => (f.Accion, f.Resultado))
            .ShouldBe([("CREAR", "EXITO"), ("ACTUALIZAR", "EXITO"), ("ACTUALIZAR", "EXITO")]);

        // Closed, the case admits a new one.
        (await paciente.PostAsJsonAsync(Ruta(caso), Cuerpo("PRESENCIAL", null), JsonOptions))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task AnUnclosedRequest_ShouldRunTenBusinessDays_AndWhenItExpiresQueueThePlazoVencidoWebhook()
    {
        string tenant = TenantNuevo("ARL_PAC_HC_VENCE");
        Guid caso = await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento(), estado: "EnAtencion");
        HttpClient paciente = await VinculadoAsync(factory, tenant, caso);

        HttpResponseMessage suscripcion = await WebhooksClientes.CrearAsync(
            WebhooksClientes.IntegracionArl(factory, tenant), "https://receptor.example.com/halsa", "plazo.vencido");
        suscripcion.StatusCode.ShouldBe(HttpStatusCode.Created, await suscripcion.Content.ReadAsStringAsync());

        HttpResponseMessage creada = await paciente.PostAsJsonAsync(Ruta(caso), Cuerpo("ELECTRONICO", "yo@example.com"), JsonOptions);
        Guid id = (await BodyAsync(creada))["id"]!.GetValue<Guid>();
        await ProcesarOutboxAsync();

        (DateTimeOffset iniciaEn, DateTimeOffset venceEn) = await FechasPlazoAsync(id);
        (venceEn - iniciaEn).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromDays(12));   // 10 business days, weekends between
        (venceEn - iniciaEn).ShouldBeLessThan(TimeSpan.FromDays(20));

        await new BarridoPlazosJob(
                factory.Services.GetRequiredService<BaseDatosWorker>(),
                new RelojFijo(venceEn.AddMinutes(1)),
                NullLogger<BarridoPlazosJob>.Instance)
            .BarrerAsync(CancellationToken.None);

        (await EstadoPlazoAsync(id)).ShouldBe("VENCIDO");

        await ProcesarOutboxAsync();
        (await ContarAsync(factory, """
            SELECT count(*) FROM integracion.entregas_webhook
            WHERE tipo = 'plazo.vencido' AND payload::text LIKE '%SOLICITUD_COPIA_HC%' AND payload::text LIKE '%' || @caso || '%'
            """, ("caso", caso.ToString()))).ShouldBe(1);
    }

    [Fact]
    public async Task Create_OnAnUnlinkedCase_ShouldAnswer404_AndWithAnInvalidBody400()
    {
        string tenant = TenantNuevo("ARL_PAC_HC_AJENO");
        Guid ajeno = await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento());
        HttpClient paciente = await VinculadoAsync(factory, tenant, await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento()));

        (await paciente.PostAsJsonAsync(Ruta(ajeno), Cuerpo(), JsonOptions)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        HttpResponseMessage sinDireccion = await paciente.PostAsJsonAsync(Ruta(ajeno), Cuerpo("CORREO_CERTIFICADO", null), JsonOptions);
        sinDireccion.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await sinDireccion.Content.ReadAsStringAsync()).ShouldContain("SolicitudCopiaHc.DireccionRequerida");

        HttpResponseMessage medio = await paciente.PostAsJsonAsync(Ruta(ajeno), Cuerpo("PALOMA", "x"), JsonOptions);
        medio.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await medio.Content.ReadAsStringAsync()).ShouldContain("SolicitudCopiaHc.MedioEntregaInvalido");
    }

    [Fact]
    public async Task TheInbox_ShouldStayInTheTenant_AndNeedItsPermission()
    {
        string tenant = TenantNuevo("ARL_PAC_HC_BANDEJA");
        Guid caso = await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento());
        HttpClient paciente = await VinculadoAsync(factory, tenant, caso);
        Guid id = (await BodyAsync(await paciente.PostAsJsonAsync(Ruta(caso), Cuerpo(), JsonOptions)))["id"]!.GetValue<Guid>();

        HttpClient otraArl = Privacidad(factory, TenantNuevo("ARL_PAC_HC_OTRA"));
        (await otraArl.GetFromJsonAsync<JsonArray>("v1/solicitudes-copia-hc", JsonOptions))!
            .Select(s => s!["id"]!.GetValue<Guid>()).ShouldNotContain(id);
        (await otraArl.PostAsync($"v1/solicitudes-copia-hc/{id}/tramitar", content: null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await Analista(factory, tenant).GetAsync("v1/solicitudes-copia-hc")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await paciente.GetAsync("v1/solicitudes-copia-hc")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        HttpResponseMessage corta = await Privacidad(factory, tenant).PostAsJsonAsync(
            $"v1/solicitudes-copia-hc/{id}/cerrar", new { resultado = "RECHAZADA", nota = "no" }, JsonOptions);
        corta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await corta.Content.ReadAsStringAsync()).ShouldContain("SolicitudCopiaHc.NotaRequerida");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> EstadoPlazoAsync(Guid solicitud)
    {
        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT estado FROM sst.plazos WHERE regla_codigo = 'SOLICITUD_COPIA_HC' AND referencia_id = @id
            """;
        cmd.Parameters.AddWithValue("id", solicitud);
        return (string?)await cmd.ExecuteScalarAsync() ?? "SIN_PLAZO";
    }

    private async Task<(DateTimeOffset IniciaEn, DateTimeOffset VenceEn)> FechasPlazoAsync(Guid solicitud)
    {
        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT inicia_en, vence_en FROM sst.plazos WHERE regla_codigo = 'SOLICITUD_COPIA_HC' AND referencia_id = @id
            """;
        cmd.Parameters.AddWithValue("id", solicitud);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue("the request did not open SOLICITUD_COPIA_HC");
        return (reader.GetFieldValue<DateTimeOffset>(0), reader.GetFieldValue<DateTimeOffset>(1));
    }

    /// <summary>The hosted outbox dispatcher is removed in tests; drain it the way it would run.</summary>
    private async Task ProcesarOutboxAsync()
    {
        var processor = new OutboxProcessor(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OutboxOptions { BatchSize = 100, MaxAttempts = 5 }),
            NullLogger<OutboxProcessor>.Instance);

        while (await processor.ProcessBatchAsync(CancellationToken.None) > 0)
        {
        }
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora.ToUniversalTime();
    }
}
