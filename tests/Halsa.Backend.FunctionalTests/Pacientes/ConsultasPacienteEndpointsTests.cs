using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using Halsa.Backend.FunctionalTests.Clinico;
using Halsa.Backend.FunctionalTests.Incapacidades;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Clinico.DictamenesSeed;
using static Halsa.Backend.FunctionalTests.Incapacidades.IncapacidadesSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;
using static Halsa.Backend.FunctionalTests.Pacientes.PacientesSeed;

namespace Halsa.Backend.FunctionalTests.Pacientes;

/// <summary>
/// SPEC 12, step 3: mis casos, el resumen, la línea de tiempo y las incapacidades del paciente. Todo sale de su
/// <c>sub</c>: un paciente sin vínculo ve listas vacías y un caso no vinculado responde <c>404</c>.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class ConsultasPacienteEndpointsTests(ApiFactory factory)
{
    [Fact]
    public async Task APatientWithoutVinculo_ShouldGetEmptyLists_And404OnAnyTimeline()
    {
        string tenant = TenantNuevo("ARL_PAC_SIN_VINC");
        Guid caso = await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento());
        HttpClient paciente = Paciente(factory, NuevoSub());

        (await ListaAsync(paciente, "v1/paciente/mis-casos")).ShouldBeEmpty();
        (await ListaAsync(paciente, "v1/paciente/incapacidades")).ShouldBeEmpty();

        JsonObject resumen = await BodyAsync(await paciente.GetAsync("v1/paciente/resumen"));
        resumen["casosActivos"]!.AsArray().ShouldBeEmpty();
        resumen["incapacidadesVigentes"]!.AsArray().ShouldBeEmpty();
        resumen["proximosHitos"]!.AsArray().ShouldBeEmpty();

        (await paciente.GetAsync($"v1/paciente/casos/{caso}/linea-tiempo")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await paciente.GetAsync($"v1/paciente/casos/{Guid.CreateVersion7()}/linea-tiempo"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MisCasos_ShouldReturnTheCaseOfTheCode_AndEveryOtherCaseOfTheDocument_FromAnyArl()
    {
        string documento = NuevoDocumento();
        string arl1 = TenantNuevo("ARL_PAC_MIS_1");
        string arl2 = TenantNuevo("ARL_PAC_MIS_2");
        Guid delCodigo = await CasoDelTrabajadorAsync(factory, arl1, documento);
        Guid deOtraArl = await CasoDelTrabajadorAsync(factory, arl2, documento, estado: "Radicado");
        Guid deOtroDocumento = await CasoDelTrabajadorAsync(factory, arl1, NuevoDocumento());

        HttpClient paciente = await VinculadoAsync(factory, arl1, delCodigo);

        JsonArray casos = await ListaAsync(paciente, "v1/paciente/mis-casos");
        casos.Select(c => c!["casoId"]!.GetValue<Guid>()).ShouldBe([delCodigo, deOtraArl], ignoreOrder: true);
        casos.Select(c => c!["arl"]!.GetValue<string>()).ShouldBe([arl1, arl2], ignoreOrder: true);
        casos.Select(c => c!["casoId"]!.GetValue<Guid>()).ShouldNotContain(deOtroDocumento);

        JsonObject primero = casos.Single(c => c!["casoId"]!.GetValue<Guid>() == delCodigo)!.AsObject();
        primero["estado"]!.GetValue<string>().ShouldBe("Validado");
        primero["tipoEvento"]!.GetValue<string>().ShouldBe("AccidenteTrabajo");
        primero["radicado"]!.GetValue<string>().ShouldStartWith("PAC-");
        primero.ContainsKey("trabajadorDocumento").ShouldBeFalse();
    }

    [Fact]
    public async Task AStaffToken_OnThePatientEndpoints_AndAPatientToken_OnTheStaffEndpoints_ShouldAnswer403()
    {
        string tenant = TenantNuevo("ARL_PAC_403");
        HttpClient analista = Analista(factory, tenant);

        foreach (string ruta in new[] { "v1/paciente/mis-casos", "v1/paciente/resumen", "v1/paciente/incapacidades",
                                        "v1/paciente/solicitudes-copia-hc", $"v1/paciente/casos/{Guid.CreateVersion7()}/linea-tiempo" })
        {
            (await analista.GetAsync(ruta)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, ruta);
        }

        HttpClient paciente = Paciente(factory, NuevoSub());
        (await paciente.GetAsync("v1/casos")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await paciente.GetAsync("v1/plazos")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task LineaTiempo_OfACaseWithADictamen_ShouldShowTheMilestones_AndNothingClinical()
    {
        // The dictamen needs ARL_A's calificador assigned; the document is still this test's own.
        Guid caso = await CasoDelTrabajadorAsync(factory, ArlA, NuevoDocumento(), estado: "EnAtencion");
        await AsignarAsync(factory, caso, ArlA, CalificadorArlASub);
        await RegistradoAsync(factory, caso);
        (await Calificador(factory).PostAsJsonAsync(RutaNotificar(caso), new JsonObject
        {
            ["notificadoEn"] = DateTimeOffset.UtcNow.AddHours(-1).ToString("O", CultureInfo.InvariantCulture),
            ["medio"] = "CORREO_CERTIFICADO"
        }, JsonOptions)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HttpClient paciente = await VinculadoAsync(factory, ArlA, caso);
        HttpResponseMessage response = await paciente.GetAsync($"v1/paciente/casos/{caso}/linea-tiempo");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();
        JsonArray hitos = JsonNode.Parse(json)!.AsArray();
        hitos.Select(h => h!["tipo"]!.GetValue<string>())
            .ShouldBe(["RADICADO", "VALIDADO", "DICTAMEN_NOTIFICADO"]);
        hitos.Select(h => h!["fecha"]!.GetValue<DateTimeOffset>()).ShouldBeInOrder();

        json.ShouldNotContain("porcentajePcl", Case.Insensitive);
        json.ShouldNotContain("fundamentos", Case.Insensitive);
        json.ShouldNotContain("32.5");
        json.ShouldNotContain("32,5");
        json.ShouldNotContain("S62");
        json.ShouldNotContain(Fundamentos[..20]);

        (await AuditoriaAsync(factory, caso)).ShouldContain(
            new AuditoriaFila("LEER", "CASO", caso.ToString(), "EXITO", ArlA, "halsa-bff-paciente"));

        // The resumen shows the controversy's deadline as an upcoming milestone.
        JsonObject resumen = await BodyAsync(await paciente.GetAsync("v1/paciente/resumen"));
        resumen["proximosHitos"]!.AsArray()
            .ShouldContain(h => h!["tipo"]!.GetValue<string>() == "VENCE_CONTROVERSIA" && h["casoId"]!.GetValue<Guid>() == caso);
    }

    [Fact]
    public async Task Incapacidades_AndResumen_ShouldShowTheVigenteOne_WithoutItsDiagnosis()
    {
        string tenant = TenantNuevo("ARL_PAC_INCAP");
        Guid caso = await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento(), estado: "EnAtencion");
        await RemitirAsync(factory, caso, tenant, Ips);
        DateOnly inicio = IncapacidadesSeed.Hoy.AddDays(-1);
        HttpResponseMessage carga = await CargarAsync(IntegracionIps(factory), caso, Datos(inicio, dias: 10, codigo: "S62.6"));
        carga.StatusCode.ShouldBe(HttpStatusCode.Created, await carga.Content.ReadAsStringAsync());

        HttpClient paciente = await VinculadoAsync(factory, tenant, caso);

        HttpResponseMessage response = await paciente.GetAsync("v1/paciente/incapacidades");
        string json = await response.Content.ReadAsStringAsync();
        JsonObject incapacidad = JsonNode.Parse(json)!.AsArray().ShouldHaveSingleItem()!.AsObject();
        incapacidad["casoId"]!.GetValue<Guid>().ShouldBe(caso);
        incapacidad["dias"]!.GetValue<int>().ShouldBe(10);
        incapacidad["fechaInicio"]!.GetValue<string>().ShouldBe(inicio.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        json.ShouldNotContain("S62");
        json.ShouldNotContain("diagnostico", Case.Insensitive);

        JsonObject resumen = await BodyAsync(await paciente.GetAsync("v1/paciente/resumen"));
        resumen["casosActivos"]!.AsArray().ShouldHaveSingleItem()!["casoId"]!.GetValue<Guid>().ShouldBe(caso);
        JsonObject vigente = resumen["incapacidadesVigentes"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
        vigente["fechaFin"]!.GetValue<string>().ShouldBe(inicio.AddDays(9).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        resumen["proximosHitos"]!.AsArray().ShouldContain(h => h!["tipo"]!.GetValue<string>() == "FIN_INCAPACIDAD");

        JsonArray hitos = await ListaAsync(paciente, $"v1/paciente/casos/{caso}/linea-tiempo");
        hitos.Select(h => h!["tipo"]!.GetValue<string>())
            .ShouldBe(["RADICADO", "VALIDADO", "REMITIDO_A_IPS", "INCAPACIDAD_REGISTRADA"]);
    }

    private static async Task<JsonArray> ListaAsync(HttpClient client, string ruta)
    {
        HttpResponseMessage response = await client.GetAsync(ruta);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonArray>(JsonOptions))!;
    }
}
