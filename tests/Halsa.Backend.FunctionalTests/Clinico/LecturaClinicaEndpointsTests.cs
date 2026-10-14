using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Clinico;

/// <summary>
/// SPEC 08, step 3: reading the HC, <c>GET /v1/casos/{casoId}/registros-clinicos[/{registroId}]</c>, behind the
/// permission, the step-up and the ABAC (assignment). Each test seeds its own case.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class LecturaClinicaEndpointsTests(ApiFactory factory)
{
    private const string CoordinadorIpsSub = "01999000-0000-7000-8000-000000000c02";

    private static string Ruta(Guid caso) => $"v1/casos/{caso}/registros-clinicos";

    private static string Ruta(Guid caso, Guid registro) => $"v1/casos/{caso}/registros-clinicos/{registro}";

    private HttpClient CalificadorConMfa() => Client(factory, ConStepUp(CalificadorArlA(), "2"));

    private HttpClient MedicoIpsConMfa() => Client(factory, ConStepUp(MedicoIps(), "2"));

    /// <summary>Writes a record through the SPEC 06 endpoint, which only needs to see the case.</summary>
    private async Task<Guid> RegistrarAsync(Guid caso, Guid? corrigeA = null, string motivo = "Control postquirúrgico")
    {
        JsonObject cuerpo = Registro(corrigeA: corrigeA);
        cuerpo["contenido"]!["motivo"] = motivo;

        HttpResponseMessage response = await Client(factory, CalificadorArlA()).PostAsJsonAsync(Ruta(caso), cuerpo, JsonOptions);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await BodyAsync(response))["registroId"]!.GetValue<Guid>();
    }

    private static async Task<JsonArray> ListaAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonArray>(JsonOptions))!;

    private async Task<List<AuditoriaFila>> LecturasAsync(Guid caso) =>
        [.. (await AuditoriaAsync(factory, caso)).Where(f => f.Accion == "LEER")];

    // ── Success ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CalificadorAssigned_WithMfa_ShouldReadTheDecryptedHc_AndLeaveALeerExitoRow()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);
        Guid registro = await RegistrarAsync(caso);
        await AsignarAsync(factory, caso, ArlA, CalificadorArlASub);

        HttpResponseMessage response = await CalificadorConMfa().GetAsync(Ruta(caso));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonObject leido = (await ListaAsync(response)).ShouldHaveSingleItem()!.AsObject();
        leido["id"]!.GetValue<Guid>().ShouldBe(registro);
        leido["tipo"]!.GetValue<string>().ShouldBe("CONCEPTO_MEDICO");
        leido["contenido"]!["motivo"]!.GetValue<string>().ShouldBe("Control postquirúrgico");
        leido["contenido"]!["diagnosticos"]![0]!["codigo"]!.GetValue<string>().ShouldBe("S62.6");
        leido["profesional"]!["registroRethus"]!.GetValue<string>().ShouldBe("RM-DEV-0001");
        leido["profesional"]!["numeroDocumento"]!.GetValue<string>().ShouldBe("52000111");
        leido["corrigeA"].ShouldBeNull();
        leido["corregidoPor"].ShouldBeNull();

        (await LecturasAsync(caso)).ShouldBe([new AuditoriaFila("LEER", "REGISTRO_CLINICO", null, "EXITO", ArlA, "halsa-backoffice")]);
    }

    [Fact]
    public async Task OneRecord_ShouldCarryItsCorrection_AndLeaveALeerRowWithItsId()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);
        Guid original = await RegistrarAsync(caso, motivo: "Con error");
        Guid correccion = await RegistrarAsync(caso, corrigeA: original, motivo: "Corregido");
        await AsignarAsync(factory, caso, ArlA, CalificadorArlASub);
        HttpClient client = CalificadorConMfa();

        HttpResponseMessage response = await client.GetAsync(Ruta(caso, original));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonObject leido = await BodyAsync(response);
        leido["contenido"]!["motivo"]!.GetValue<string>().ShouldBe("Con error");
        leido["corregidoPor"]!.GetValue<Guid>().ShouldBe(correccion);
        (await BodyAsync(await client.GetAsync(Ruta(caso, correccion))))["corrigeA"]!.GetValue<Guid>().ShouldBe(original);

        (await LecturasAsync(caso))[0].ShouldBe(
            new AuditoriaFila("LEER", "REGISTRO_CLINICO", original.ToString(), "EXITO", ArlA, "halsa-backoffice"));
    }

    [Fact]
    public async Task MedicoIps_AssignedByTheCoordinator_ShouldReadTheHc_Then404OnceTheRemissionIsRevoked()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA, remitirA: Ips);
        await RegistrarAsync(caso);
        HttpClient coordinador = Client(factory, new Dictionary<string, object>
        {
            ["sub"] = CoordinadorIpsSub,
            ["iss"] = TestAuthenticationHandler.DefaultIssuer,
            ["actor_tipo"] = "ips",
            ["ips_id"] = Ips,
            ["permissions"] = new[] { "casos:read", "casos:asignar" }
        });
        (await coordinador.PostAsJsonAsync($"v1/casos/{caso}/asignaciones", new { sub = MedicoIpsSub }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        HttpClient medico = MedicoIpsConMfa();

        HttpResponseMessage antes = await medico.GetAsync(Ruta(caso));
        await RevocarAsync(factory, caso, ArlA);
        HttpResponseMessage despues = await medico.GetAsync(Ruta(caso));

        antes.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ListaAsync(antes)).Count.ShouldBe(1);
        despues.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(despues)).ShouldBe("Clinico.CasoNoEncontrado");
    }

    // ── DICTAMEN_PCL ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TheHc_ShouldNotIncludeDictamenPclRecords_AndOneShouldAnswer404()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);
        Guid concepto = await RegistrarAsync(caso);
        Guid dictamen = await RegistrarAsync(caso, motivo: "Fundamentos del dictamen");
        await RetipificarAsync(factory, dictamen, "DictamenPcl");
        await AsignarAsync(factory, caso, ArlA, CalificadorArlASub);
        HttpClient client = CalificadorConMfa();

        HttpResponseMessage lista = await client.GetAsync(Ruta(caso));
        HttpResponseMessage uno = await client.GetAsync(Ruta(caso, dictamen));

        (await ListaAsync(lista)).Select(r => r!["id"]!.GetValue<Guid>()).ShouldBe([concepto]);
        uno.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(uno)).ShouldBe("Clinico.RegistroNoEncontrado");
    }

    // ── Permission and step-up (middleware) ───────────────────────────────────

    [Fact]
    public async Task AnalistaArl_ShouldGet403_AndEachAttemptShouldLeaveADenegadoRow()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);
        Guid registro = await RegistrarAsync(caso);
        HttpClient analista = Client(factory, ConStepUp(AnalistaArlA(), "2"));

        HttpResponseMessage lista = await analista.GetAsync(Ruta(caso));
        HttpResponseMessage uno = await analista.GetAsync(Ruta(caso, registro));

        lista.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        uno.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        List<AuditoriaFila> lecturas = await LecturasAsync(caso);
        lecturas.Count.ShouldBe(2);
        lecturas.ShouldAllBe(f => f.Recurso == "REGISTRO_CLINICO" && f.Resultado == "DENEGADO");
    }

    [Fact]
    public async Task CalificadorAssigned_WithAcr1_ShouldGet401_WithTheStepUpChallenge()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);
        await AsignarAsync(factory, caso, ArlA, CalificadorArlASub);

        HttpResponseMessage response = await Client(factory, ConStepUp(CalificadorArlA(), "1")).GetAsync(Ruta(caso));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("error=\"insufficient_user_authentication\"");
    }

    [Fact]
    public async Task AnIpsIntegrationToken_ShouldGet403()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA, remitirA: Ips);
        Guid registro = await RegistrarAsync(caso);

        (await IntegracionIps(factory).GetAsync(Ruta(caso))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await IntegracionIps(factory).GetAsync(Ruta(caso, registro))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ── ABAC (endpoint) ───────────────────────────────────────────────────────

    [Fact]
    public async Task CalificadorNotAssigned_WithMfa_ShouldGet403NoAsignado_AndADenegadoRowWithTheCasesTenant()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);
        await RegistrarAsync(caso);

        HttpResponseMessage response = await CalificadorConMfa().GetAsync(Ruta(caso));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.NoAsignado");
        (await LecturasAsync(caso)).ShouldBe([
            new AuditoriaFila("LEER", "REGISTRO_CLINICO", caso.ToString(), "DENEGADO", ArlA, "halsa-backoffice")]);
    }

    [Fact]
    public async Task Calificador_OnAnArlBCase_ShouldGet404()
    {
        Guid casoB = await SeedCasoAsync(factory, ArlB);
        await AsignarAsync(factory, casoB, ArlB, CalificadorArlASub);

        HttpResponseMessage response = await CalificadorConMfa().GetAsync(Ruta(casoB));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.CasoNoEncontrado");
        (await LecturasAsync(casoB)).ShouldHaveSingleItem().Resultado.ShouldBe("DENEGADO");
    }

    [Fact]
    public async Task MedicoIps_AssignedByTheArlInsteadOfItsIps_ShouldGet403NoAsignado()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA, remitirA: Ips);
        await AsignarAsync(factory, caso, ArlA, MedicoIpsSub);

        HttpResponseMessage response = await MedicoIpsConMfa().GetAsync(Ruta(caso));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.NoAsignado");
    }

    [Fact]
    public async Task ARecordOfAnotherCase_ShouldAnswer404()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);
        Guid otroCaso = await SeedCasoAsync(factory, ArlA);
        Guid registroDelOtro = await RegistrarAsync(otroCaso);
        await AsignarAsync(factory, caso, ArlA, CalificadorArlASub);

        HttpResponseMessage response = await CalificadorConMfa().GetAsync(Ruta(caso, registroDelOtro));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.RegistroNoEncontrado");
    }
}
