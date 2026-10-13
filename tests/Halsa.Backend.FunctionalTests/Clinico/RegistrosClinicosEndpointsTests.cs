using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Clinico;

/// <summary>
/// SPEC 06, step 3: <c>POST /v1/casos/{casoId}/registros-clinicos</c> from the backoffice. Each test seeds its
/// own case, so it never depends on the order of the others.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class RegistrosClinicosEndpointsTests(ApiFactory factory)
{
    private static string Ruta(Guid caso) => $"v1/casos/{caso}/registros-clinicos";

    private Task<HttpResponseMessage> PostAsync(Dictionary<string, object> actor, Guid caso, JsonObject cuerpo) =>
        Client(factory, actor).PostAsJsonAsync(Ruta(caso), cuerpo, JsonOptions);

    // ── Success ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task MedicoIps_WithRemision_ShouldAnswer201_AndSignWithTheTokenIdentity()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA, remitirA: Ips);

        HttpResponseMessage response = await PostAsync(MedicoIps(), caso, Registro());

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonObject body = await BodyAsync(response);
        Guid registroId = body["registroId"]!.GetValue<Guid>();
        body["casoId"]!.GetValue<Guid>().ShouldBe(caso);
        body["registradoEn"]!.GetValue<DateTimeOffset>().ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        response.Headers.Location!.ToString().ShouldBe($"/v1/casos/{caso}/registros-clinicos/{registroId}");

        RegistroFila registro = (await RegistrosAsync(factory, caso)).ShouldHaveSingleItem();
        registro.Id.ShouldBe(registroId);
        registro.TenantId.ShouldBe(ArlA);
        registro.Tipo.ShouldBe("ConceptoMedico");
        registro.Rethus.ShouldBe("RM-DEV-0002");
        registro.ProfesionalSub.ShouldBe(MedicoIpsSub);
        registro.ContenidoKeyId.ShouldBe(await LlaveContenidoAsync(factory, ArlA));

        AuditoriaFila auditoria = (await AuditoriaAsync(factory, caso)).ShouldHaveSingleItem();
        auditoria.ShouldBe(new AuditoriaFila(
            "CREAR", "REGISTRO_CLINICO", registroId.ToString(), "EXITO", ArlA, "halsa-backoffice"));
    }

    [Theory]
    [InlineData("EVOLUCION", "Evolucion")]
    [InlineData("CONCEPTO_REHABILITACION", "ConceptoRehabilitacion")]
    public async Task OtherAdmittedTypes_ShouldAnswer201(string tipo, string guardado)
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);

        HttpResponseMessage response = await PostAsync(CalificadorArlA(), caso, Registro(tipo));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await RegistrosAsync(factory, caso)).ShouldHaveSingleItem().Tipo.ShouldBe(guardado);
    }

    [Theory]
    [InlineData("S62")]
    [InlineData("S62.6")]
    public async Task ValidCie10Codes_ShouldAnswer201(string codigo)
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);

        HttpResponseMessage response = await PostAsync(CalificadorArlA(), caso, Registro(codigo: codigo));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CorrigeA_ShouldCreateANewRecord_LeaveTheOriginal_AndAuditCorregir()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);
        Guid original = (await BodyAsync(await PostAsync(CalificadorArlA(), caso, Registro())))["registroId"]!.GetValue<Guid>();

        HttpResponseMessage response = await PostAsync(CalificadorArlA(), caso, Registro(corrigeA: original));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid correccion = (await BodyAsync(response))["registroId"]!.GetValue<Guid>();

        List<RegistroFila> registros = await RegistrosAsync(factory, caso);
        registros.Count.ShouldBe(2);
        registros.Single(r => r.Id == original).CorrigeA.ShouldBeNull();
        registros.Single(r => r.Id == correccion).CorrigeA.ShouldBe(original);

        List<AuditoriaFila> auditoria = await AuditoriaAsync(factory, caso);
        auditoria.Select(a => (a.Accion, a.RecursoId, a.Resultado, a.TenantId)).ShouldBe(
        [
            ("CREAR", original.ToString(), "EXITO", ArlA),
            ("CORREGIR", correccion.ToString(), "EXITO", ArlA)
        ]);
    }

    // ── Identity and authorization ────────────────────────────────────────────

    [Fact]
    public async Task WithClinicoWrite_ButWithoutRethus_ShouldAnswer403_ProfesionalSinRegistro()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA, remitirA: Ips);
        Dictionary<string, object> sinRethus = MedicoIps();
        sinRethus.Remove("rethus");

        HttpResponseMessage response = await PostAsync(sinRethus, caso, Registro());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.ProfesionalSinRegistro");
        (await RegistrosAsync(factory, caso)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnalistaArlA_WithoutClinicoWrite_ShouldAnswer403_AndLeaveADenegadoRow()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);

        HttpResponseMessage response = await PostAsync(AnalistaArlA(), caso, Registro());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        AuditoriaFila denegado = (await AuditoriaAsync(factory, caso)).ShouldHaveSingleItem();
        denegado.Accion.ShouldBe("CREAR");
        denegado.Recurso.ShouldBe("REGISTRO_CLINICO");
        denegado.Resultado.ShouldBe("DENEGADO");
        (await RegistrosAsync(factory, caso)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIntegrationActor_WithClinicoWrite_ShouldAnswer403_ActorNoAutorizado()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA, remitirA: Ips);
        Dictionary<string, object> integracion = MedicoIps();
        integracion["actor_tipo"] = "integracion";

        HttpResponseMessage response = await PostAsync(integracion, caso, Registro());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.ActorNoAutorizado");
    }

    [Fact]
    public async Task CalificadorArlA_OnAnArlBCase_ShouldAnswer404()
    {
        Guid casoB = await SeedCasoAsync(factory, ArlB);

        HttpResponseMessage response = await PostAsync(CalificadorArlA(), casoB, Registro());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.CasoNoEncontrado");
        (await RegistrosAsync(factory, casoB)).ShouldBeEmpty();
    }

    [Fact]
    public async Task MedicoIps_WithoutRemision_ShouldAnswer404()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);

        HttpResponseMessage response = await PostAsync(MedicoIps(), caso, Registro());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.CasoNoEncontrado");
    }

    [Fact]
    public async Task CorrigeA_OfARecordOfAnotherCase_ShouldAnswer404_RegistroNoEncontrado()
    {
        Guid otroCaso = await SeedCasoAsync(factory, ArlA);
        Guid ajeno = (await BodyAsync(await PostAsync(CalificadorArlA(), otroCaso, Registro())))["registroId"]!.GetValue<Guid>();
        Guid caso = await SeedCasoAsync(factory, ArlA);

        HttpResponseMessage response = await PostAsync(CalificadorArlA(), caso, Registro(corrigeA: ajeno));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.RegistroNoEncontrado");
    }

    // ── Rules ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("DICTAMEN_PCL")]
    [InlineData("REPORTE_INICIAL")]
    [InlineData("concepto_medico")]
    public async Task ATypeOutsideTheThreeAdmitted_ShouldAnswer400(string tipo)
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);

        HttpResponseMessage response = await PostAsync(CalificadorArlA(), caso, Registro(tipo));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyAsync(response))["errors"]!.AsArray()
            .Select(e => e!["code"]!.GetValue<string>()).ShouldBe(["Clinico.TipoInvalido"]);
    }

    [Theory]
    [InlineData("ABC.1")]
    [InlineData("S626")]
    [InlineData("s62.6")]
    public async Task AnInvalidCie10Code_ShouldAnswer400(string codigo)
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);

        HttpResponseMessage response = await PostAsync(CalificadorArlA(), caso, Registro(codigo: codigo));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyAsync(response))["errors"]!.AsArray()
            .Select(e => e!["code"]!.GetValue<string>()).ShouldBe(["Clinico.DiagnosticoCodigoInvalido"]);
    }

    [Fact]
    public async Task ADiagnosisOutsideCie10_ShouldAnswer400()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);
        JsonObject cuerpo = Registro();
        cuerpo["contenido"]!["diagnosticos"]![0]!["sistema"] = "RES156-TIPO-LESION";

        HttpResponseMessage response = await PostAsync(CalificadorArlA(), caso, cuerpo);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AContenidoWithOnlyObservaciones_ShouldAnswer400()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);
        JsonObject cuerpo = Registro();
        cuerpo["contenido"] = new JsonObject { ["observaciones"] = "Solo una observación." };

        HttpResponseMessage response = await PostAsync(CalificadorArlA(), caso, cuerpo);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyAsync(response))["errors"]!.AsArray()
            .Select(e => e!["code"]!.GetValue<string>()).ShouldContain("Clinico.ContenidoVacio");
    }

    [Fact]
    public async Task AFieldOver8000Characters_ShouldAnswer400()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);
        JsonObject cuerpo = Registro();
        cuerpo["contenido"]!["anamnesis"] = new string('a', 8001);

        HttpResponseMessage response = await PostAsync(CalificadorArlA(), caso, cuerpo);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyAsync(response))["errors"]!.AsArray()
            .Select(e => e!["code"]!.GetValue<string>()).ShouldBe(["Clinico.CampoDemasiadoLargo"]);
    }

    [Fact]
    public async Task AFutureAtendidoEn_ShouldAnswer400()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);

        HttpResponseMessage response = await PostAsync(
            CalificadorArlA(), caso, Registro(atendidoEn: DateTimeOffset.UtcNow.AddHours(2)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AtendidoEn_TwoDaysBeforeTheEvent_ShouldAnswer400_FechaAtencionAnteriorAlEvento()
    {
        DateTimeOffset fechaEvento = DateTimeOffset.UtcNow.AddDays(-10);
        Guid caso = await SeedCasoAsync(factory, ArlA, fechaEvento: fechaEvento);

        HttpResponseMessage response = await PostAsync(
            CalificadorArlA(), caso, Registro(atendidoEn: fechaEvento.AddDays(-2)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.FechaAtencionAnteriorAlEvento");
    }

    [Fact]
    public async Task ARejectedCase_ShouldAnswer409_CasoNoAdmiteRegistros()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA, estado: "Rechazado");

        HttpResponseMessage response = await PostAsync(CalificadorArlA(), caso, Registro());

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.CasoNoAdmiteRegistros");
        (await RegistrosAsync(factory, caso)).ShouldBeEmpty();
    }
}
