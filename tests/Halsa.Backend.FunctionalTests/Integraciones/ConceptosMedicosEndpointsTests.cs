using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using Halsa.Backend.FunctionalTests.Clinico;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Integraciones;

/// <summary>
/// SPEC 06, step 5: <c>POST /v1/integraciones/conceptos-medicos</c> end to end, as <c>int-ips-110011234501</c>. Each
/// test seeds its own case and uses its own key.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class ConceptosMedicosEndpointsTests(ApiFactory factory)
{
    private const string Conceptos = "v1/integraciones/conceptos-medicos";

    /// <summary>The integration body of the spec, on <paramref name="casoId"/>.</summary>
    private static JsonObject Concepto(
        Guid casoId, string tipo = "CONCEPTO_MEDICO", string motivo = "Control postquirúrgico", Guid? corrigeA = null)
    {
        // The backoffice body plus what only the integration carries: version, case and professional.
        JsonObject cuerpo = Registro(tipo, corrigeA: corrigeA);
        cuerpo["contenido"]!["motivo"] = motivo;
        cuerpo["atendidoEn"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToOffset(TimeSpan.FromHours(-5))
            .ToString("O", CultureInfo.InvariantCulture);
        cuerpo["version"] = "1.0";
        cuerpo["casoId"] = casoId.ToString();
        cuerpo["profesional"] = new JsonObject
        {
            ["identificacion"] = new JsonObject { ["tipo"] = "CC", ["numero"] = "79111222" },
            ["nombreCompleto"] = "Ana Ruiz",
            ["registroRethus"] = "RM-12345",
            ["especialidad"] = "Ortopedia"
        };

        return cuerpo;
    }

    // ── Success ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnAnArlBCase_WithVigenteRemision_ShouldAnswer201_WithTheCasesTenantAndDek()
    {
        Guid caso = await SeedCasoAsync(factory, ArlB, remitirA: Ips);

        HttpResponseMessage response = await PostAsync(IntegracionIps(factory), Conceptos, Concepto(caso));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonObject body = await BodyAsync(response);
        Guid registroId = body["registroId"]!.GetValue<Guid>();
        body["casoId"]!.GetValue<Guid>().ShouldBe(caso);
        response.Headers.Location!.ToString().ShouldBe($"/v1/casos/{caso}/registros-clinicos/{registroId}");

        RegistroFila registro = (await RegistrosAsync(factory, caso)).ShouldHaveSingleItem();
        registro.Id.ShouldBe(registroId);
        registro.TenantId.ShouldBe(ArlB);
        registro.ContenidoKeyId.ShouldBe(await LlaveContenidoAsync(factory, ArlB));
        registro.Rethus.ShouldBe("RM-12345");
        registro.ProfesionalSub.ShouldBe("01999000-0000-7000-8000-00000000b001");

        AuditoriaFila auditoria = (await AuditoriaAsync(factory, caso)).ShouldHaveSingleItem();
        auditoria.ShouldBe(new AuditoriaFila(
            "CREAR", "REGISTRO_CLINICO", registroId.ToString(), "EXITO", ArlB, "int-ips-110011234501"));
    }

    // ── Visibility ────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnACaseWithoutRemision_ShouldAnswer404_CasoNoEncontrado()
    {
        Guid caso = await SeedCasoAsync(factory, ArlB);

        HttpResponseMessage response = await PostAsync(IntegracionIps(factory), Conceptos, Concepto(caso));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.CasoNoEncontrado");
        (await RegistrosAsync(factory, caso)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AfterRevokingTheRemision_ANewPost_ShouldAnswer404()
    {
        Guid caso = await SeedCasoAsync(factory, ArlB, remitirA: Ips);
        HttpClient client = IntegracionIps(factory);
        (await PostAsync(client, Conceptos, Concepto(caso))).StatusCode.ShouldBe(HttpStatusCode.Created);

        await RevocarAsync(factory, caso, ArlB);

        HttpResponseMessage response = await PostAsync(client, Conceptos, Concepto(caso));
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.CasoNoEncontrado");
    }

    [Fact]
    public async Task AnArlIntegration_WithoutTheClinicalPermission_ShouldAnswer403()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);

        HttpResponseMessage response = await PostAsync(IntegracionArl(factory, ArlA), Conceptos, Concepto(caso));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnArlIntegration_WithTheClinicalPermission_ShouldAnswer403_ActorNoAutorizado()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);
        HttpClient client = Client(factory, new Dictionary<string, object>
        {
            ["sub"] = "01999000-0000-7000-8000-00000000c001",
            ["iss"] = TestAuthenticationHandler.DefaultIssuer,
            ["azp"] = "int-arl-ARL_A",
            ["actor_tipo"] = "integracion",
            ["tenant_id"] = ArlA,
            ["permissions"] = new[] { "integracion:clinico:write" }
        });

        HttpResponseMessage response = await PostAsync(client, Conceptos, Concepto(caso));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.ActorNoAutorizado");
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SameKey_SameBody_ShouldReplay201_WithTheSameRegistro_AndASingleRow()
    {
        Guid caso = await SeedCasoAsync(factory, ArlB, remitirA: Ips);
        HttpClient client = IntegracionIps(factory);
        JsonObject cuerpo = Concepto(caso);
        Guid llave = Guid.NewGuid();

        JsonObject primera = await BodyAsync(await PostAsync(client, Conceptos, cuerpo, llave));
        HttpResponseMessage segunda = await PostAsync(client, Conceptos, cuerpo, llave);

        segunda.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid registroId = primera["registroId"]!.GetValue<Guid>();
        (await BodyAsync(segunda))["registroId"]!.GetValue<Guid>().ShouldBe(registroId);
        segunda.Headers.Location!.ToString().ShouldBe($"/v1/casos/{caso}/registros-clinicos/{registroId}");
        (await RegistrosAsync(factory, caso)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SameKey_AnotherBody_ShouldAnswer409_IdempotenciaConflicto()
    {
        Guid caso = await SeedCasoAsync(factory, ArlB, remitirA: Ips);
        HttpClient client = IntegracionIps(factory);
        Guid llave = Guid.NewGuid();

        await PostAsync(client, Conceptos, Concepto(caso, motivo: "Primero"), llave);
        HttpResponseMessage response = await PostAsync(client, Conceptos, Concepto(caso, motivo: "Otro"), llave);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).ShouldBe("Integracion.IdempotenciaConflicto");
    }

    [Fact]
    public async Task KeyK_UsedByAnArlFurat_AndByAnIpsConcepto_ShouldNotCollide()
    {
        Guid llave = Guid.NewGuid();

        HttpResponseMessage furat = await PostAsync(
            IntegracionArl(factory, ArlA), "v1/integraciones/furat", Furat($"K-{Guid.NewGuid():N}"[..30]), llave);

        Guid caso = await SeedCasoAsync(factory, ArlA, remitirA: Ips);
        HttpResponseMessage concepto = await PostAsync(IntegracionIps(factory), Conceptos, Concepto(caso), llave);

        furat.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        concepto.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task WithoutIdempotencyKey_ShouldAnswer400()
    {
        Guid caso = await SeedCasoAsync(factory, ArlB, remitirA: Ips);

        HttpResponseMessage response = await PostAsync(IntegracionIps(factory), Conceptos, Concepto(caso), sinLlave: true);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).ShouldBe("Integracion.IdempotencyKeyInvalida");
    }

    // ── Rules ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("DICTAMEN_PCL")]
    [InlineData("REPORTE_INICIAL")]
    public async Task ATypeOutsideTheThreeAdmitted_ShouldAnswer400_WithTheErrorOnTipo(string tipo)
    {
        Guid caso = await SeedCasoAsync(factory, ArlB, remitirA: Ips);

        HttpResponseMessage response = await PostAsync(IntegracionIps(factory), Conceptos, Concepto(caso, tipo));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyAsync(response))["errors"]!.AsObject().Select(e => e.Key).ShouldBe(["tipo"]);
    }

    [Fact]
    public async Task OnARejectedCase_ShouldAnswer409_CasoNoAdmiteRegistros()
    {
        Guid caso = await SeedCasoAsync(factory, ArlB, remitirA: Ips, estado: "Rechazado");

        HttpResponseMessage response = await PostAsync(IntegracionIps(factory), Conceptos, Concepto(caso));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.CasoNoAdmiteRegistros");
    }

    [Fact]
    public async Task CorrigeA_ShouldAnswer201_AndAuditCorregir()
    {
        Guid caso = await SeedCasoAsync(factory, ArlB, remitirA: Ips);
        HttpClient client = IntegracionIps(factory);
        Guid original = (await BodyAsync(await PostAsync(client, Conceptos, Concepto(caso))))["registroId"]!.GetValue<Guid>();

        HttpResponseMessage response = await PostAsync(client, Conceptos, Concepto(caso, corrigeA: original));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid correccion = (await BodyAsync(response))["registroId"]!.GetValue<Guid>();
        (await RegistrosAsync(factory, caso)).Single(r => r.Id == correccion).CorrigeA.ShouldBe(original);
        (await AuditoriaAsync(factory, caso)).Select(a => (a.Accion, a.TenantId)).ShouldBe(
            [("CREAR", ArlB), ("CORREGIR", ArlB)]);
    }
}
