using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Clinico.DictamenesSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Clinico;

/// <summary>
/// SPEC 08, step 6: <c>GET /v1/casos/{casoId}/dictamen-pcl</c> (the only way out of a <c>DICTAMEN_PCL</c> record) and
/// <c>POST …/dictamen-pcl/notificar</c>. Each test seeds its own case.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class DictamenPclLecturaYNotificacionTests(ApiFactory factory)
{
    private static JsonObject Notificacion(DateTimeOffset? notificadoEn = null, string medio = "CORREO_CERTIFICADO") => new()
    {
        ["notificadoEn"] = (notificadoEn ?? DateTimeOffset.UtcNow.AddHours(-1)).ToString("O", CultureInfo.InvariantCulture),
        ["medio"] = medio
    };

    private Task<HttpResponseMessage> NotificarAsync(Guid caso, JsonObject? cuerpo = null) =>
        Calificador(factory).PostAsJsonAsync(RutaNotificar(caso), cuerpo ?? Notificacion(), JsonOptions);

    private async Task<List<AuditoriaFila>> LecturasAsync(Guid caso) =>
        [.. (await AuditoriaAsync(factory, caso)).Where(f => f.Accion == "LEER" && f.Recurso == "REGISTRO_CLINICO")];

    // ── GET: success ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Calificador_WithARecentStepUp_ShouldReadTheWholeDictamen_NotCached_AndLeaveALeerExitoRow()
    {
        Guid caso = await CasoAsignadoAsync(factory);
        Guid id = (await RegistradoAsync(factory, caso))["id"]!.GetValue<Guid>();

        HttpResponseMessage response = await Calificador(factory).GetAsync(RutaDictamen(caso));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        JsonObject body = await BodyAsync(response);
        body["id"]!.GetValue<Guid>().ShouldBe(id);
        body["porcentajePcl"]!.GetValue<decimal>().ShouldBe(32.50m);
        body["fundamentos"]!.GetValue<string>().ShouldBe(Fundamentos);
        body["componentes"]!["rolLaboralYOtrasAreas"]!.GetValue<decimal>().ShouldBe(12.50m);
        body["diagnosticos"]![0]!["codigo"]!.GetValue<string>().ShouldBe("S62.6");
        body["calificador"]!["identificacion"]!["numero"]!.GetValue<string>().ShouldBe("52000111");
        body["estadoNotificacion"]!.GetValue<string>().ShouldBe("PENDIENTE");

        (await LecturasAsync(caso)).ShouldBe([new AuditoriaFila("LEER", "REGISTRO_CLINICO", null, "EXITO", ArlA, "halsa-backoffice")]);
    }

    [Fact]
    public async Task AfterACorrection_ShouldReadTheNewDictamen()
    {
        Guid caso = await CasoAsignadoAsync(factory);
        Guid anterior = (await RegistradoAsync(factory, caso))["id"]!.GetValue<Guid>();
        Guid nuevo = (await RegistradoAsync(factory, caso, Dictamen(40.00m, 25.00m, 15.00m, corrigeA: anterior)))["id"]!.GetValue<Guid>();

        JsonObject body = await BodyAsync(await Calificador(factory).GetAsync(RutaDictamen(caso)));

        body["id"]!.GetValue<Guid>().ShouldBe(nuevo);
        body["porcentajePcl"]!.GetValue<decimal>().ShouldBe(40.00m);
        body["corrigeA"]!.GetValue<Guid>().ShouldBe(anterior);
    }

    [Fact]
    public async Task ACaseWithoutDictamen_ShouldAnswer404NoEncontrado()
    {
        Guid caso = await CasoAsignadoAsync(factory);

        HttpResponseMessage response = await Calificador(factory).GetAsync(RutaDictamen(caso));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).ShouldBe("Dictamen.NoEncontrado");
    }

    // ── GET: denials ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalistaArl_ShouldGet403_AndADenegadoRow()
    {
        Guid caso = await CasoAsignadoAsync(factory);
        await RegistradoAsync(factory, caso);

        HttpResponseMessage response = await Client(factory, ConStepUp(AnalistaArlA(), "2", 1)).GetAsync(RutaDictamen(caso));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LecturasAsync(caso)).ShouldHaveSingleItem().Resultado.ShouldBe("DENEGADO");
    }

    [Fact]
    public async Task CalificadorAssigned_WithAcr1_ShouldGet401InsufficientUserAuthentication()
    {
        Guid caso = await CasoAsignadoAsync(factory);
        await RegistradoAsync(factory, caso);

        HttpResponseMessage response = await Client(factory, ConStepUp(CalificadorArlA(), "1", 1)).GetAsync(RutaDictamen(caso));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("error=\"insufficient_user_authentication\"");
    }

    [Fact]
    public async Task CalificadorAssigned_WithAcr2_ButAuthTime20MinutesAgo_ShouldGet401WithMaxAge900()
    {
        Guid caso = await CasoAsignadoAsync(factory);
        await RegistradoAsync(factory, caso);

        HttpResponseMessage response = await Calificador(factory, autenticadoHaceMinutos: 20).GetAsync(RutaDictamen(caso));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("max_age=\"900\"");
    }

    [Fact]
    public async Task CalificadorNotAssigned_ShouldGet403NoAsignado()
    {
        Guid caso = await CasoAsignadoAsync(factory);
        await RegistradoAsync(factory, caso);
        Guid otroCaso = await SeedCasoAsync(factory, ArlA);

        HttpResponseMessage response = await Calificador(factory).GetAsync(RutaDictamen(otroCaso));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.NoAsignado");
    }

    [Fact]
    public async Task Calificador_OnAnArlBCase_ShouldGet404()
    {
        Guid casoB = await SeedCasoAsync(factory, ArlB);
        await AsignarAsync(factory, casoB, ArlB, CalificadorArlASub);

        HttpResponseMessage response = await Calificador(factory).GetAsync(RutaDictamen(casoB));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.CasoNoEncontrado");
    }

    [Fact]
    public async Task AnIpsIntegrationToken_ShouldGet403()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA, remitirA: Ips);

        (await IntegracionIps(factory).GetAsync(RutaDictamen(caso))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ── Notificar ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Notificar_ShouldAnswer204_LeaveTheEventInTheOutbox_AndThenRefuseCorrections()
    {
        Guid caso = await CasoAsignadoAsync(factory);
        Guid id = (await RegistradoAsync(factory, caso))["id"]!.GetValue<Guid>();

        HttpResponseMessage response = await NotificarAsync(caso, Notificacion(medio: "PERSONAL"));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        JsonObject leido = await BodyAsync(await Calificador(factory).GetAsync(RutaDictamen(caso)));
        leido["estadoNotificacion"]!.GetValue<string>().ShouldBe("NOTIFICADO");
        (await EscalarAsync<string>(factory, "SELECT medio_notificacion FROM atel.dictamenes_pcl WHERE id = @id", id))
            .ShouldBe("PERSONAL");

        (await ContarAsync(factory, """
            SELECT count(*) FROM outbox.outbox_messages
            WHERE type LIKE '%DictamenPclNotificadoIntegrationEvent' AND content::text LIKE '%' || @id || '%'
            """, ("id", id.ToString()))).ShouldBe(1);

        (await AuditoriaAsync(factory, caso)).ShouldContain(
            new AuditoriaFila("ACTUALIZAR", "CASO", caso.ToString(), "EXITO", ArlA, "halsa-backoffice"));

        HttpResponseMessage correccion = await Calificador(factory)
            .PostAsJsonAsync(RutaDictamen(caso), Dictamen(corrigeA: id), JsonOptions);
        correccion.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(correccion)).ShouldBe("Dictamen.YaNotificado");
    }

    [Fact]
    public async Task NotificarTwice_ShouldAnswer409YaNotificado()
    {
        Guid caso = await CasoAsignadoAsync(factory);
        await RegistradoAsync(factory, caso);
        (await NotificarAsync(caso)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HttpResponseMessage segunda = await NotificarAsync(caso);

        segunda.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(segunda)).ShouldBe("Dictamen.YaNotificado");
    }

    [Fact]
    public async Task NotificarInTheFuture_OrBeforeTheDictamen_ShouldAnswer400()
    {
        Guid caso = await CasoAsignadoAsync(factory);
        await RegistradoAsync(factory, caso);

        HttpResponseMessage futura = await NotificarAsync(caso, Notificacion(DateTimeOffset.UtcNow.AddHours(1)));
        HttpResponseMessage previa = await NotificarAsync(caso, Notificacion(DateTimeOffset.UtcNow.AddDays(-5)));

        futura.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(futura)).ShouldBe("Dictamen.NotificacionFueraDeRango");
        previa.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(previa)).ShouldBe("Dictamen.NotificacionFueraDeRango");
    }

    [Fact]
    public async Task NotificarWithAnUnknownMedio_ShouldAnswer400()
    {
        Guid caso = await CasoAsignadoAsync(factory);
        await RegistradoAsync(factory, caso);

        HttpResponseMessage response = await NotificarAsync(caso, Notificacion(medio: "FAX"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Dictamen.MedioInvalido");
    }

    [Fact]
    public async Task NotificarWithoutADictamen_ShouldAnswer404()
    {
        Guid caso = await CasoAsignadoAsync(factory);

        HttpResponseMessage response = await NotificarAsync(caso);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).ShouldBe("Dictamen.NoEncontrado");
    }
}
