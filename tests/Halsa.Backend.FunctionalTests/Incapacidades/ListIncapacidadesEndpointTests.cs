using System.Net;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Incapacidades.IncapacidadesSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Incapacidades;

/// <summary>
/// SPEC 07, step 8: <c>GET /v1/casos/{casoId}/incapacidades</c>. Each test seeds its own case and loads its
/// incapacidades through the IPS integration.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class ListIncapacidadesEndpointTests(ApiFactory factory)
{
    private async Task<Guid> CargadaAsync(Guid caso, JsonObject datos)
    {
        HttpResponseMessage response = await CargarAsync(IntegracionIps(factory), caso, datos);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await BodyAsync(response))["id"]!.GetValue<Guid>();
    }

    private Task<HttpResponseMessage> ListarAsync(Dictionary<string, object> actor, Guid caso) =>
        Client(factory, actor).GetAsync(new Uri(Ruta(caso), UriKind.Relative));

    [Fact]
    public async Task AnalistaArlA_ShouldListByStartDate_WithoutDiagnosisNorProfessional_AndAuditTheRead()
    {
        Guid caso = await CasoRemitidoAsync(factory);
        DateOnly inicio = Hoy.AddDays(-60);
        Guid inicial = await CargadaAsync(caso, Datos(inicio, dias: 10, codigo: "S62.6"));
        Guid prorroga = await CargadaAsync(caso,
            Datos(inicio.AddDays(10), dias: 5, tipo: "PRORROGA", prorrogaDe: inicial, codigo: "S62.6"));

        HttpResponseMessage response = await ListarAsync(AnalistaArlA(), caso);

        string cuerpo = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, cuerpo);
        cuerpo.ShouldNotContain("S62");
        cuerpo.ShouldNotContain("confidencial");
        cuerpo.ShouldNotContain("profesional", Case.Insensitive);
        cuerpo.ShouldNotContain("RM-12345");

        JsonArray lista = JsonNode.Parse(cuerpo)!.AsArray();
        lista.Select(i => i!["id"]!.GetValue<Guid>()).ShouldBe([inicial, prorroga]);
        lista[1]!["diasAcumulados"]!.GetValue<int>().ShouldBe(15);
        lista[1]!["alertas"]!.AsArray().ShouldNotBeNull();

        (await AuditoriaAsync(factory, caso))
            .Count(f => f is { Accion: "LEER", Recurso: "INCAPACIDAD", Resultado: "EXITO", TenantId: ArlA })
            .ShouldBe(1);
    }

    [Fact]
    public async Task ACaseWithoutIncapacidades_ShouldAnswerAnEmptyList()
    {
        Guid caso = await CasoRemitidoAsync(factory);

        HttpResponseMessage response = await ListarAsync(AnalistaArlA(), caso);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIpsPerson_OfTheRemittedIps_ShouldListThem()
    {
        Guid caso = await CasoRemitidoAsync(factory);
        await CargadaAsync(caso, Datos());
        HttpResponseMessage response = await ListarAsync(MedicoIps(), caso);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnArlBPerson_OnAnArlACase_ShouldAnswer404()
    {
        Guid caso = await CasoRemitidoAsync(factory, ArlA);
        await CargadaAsync(caso, Datos());
        Dictionary<string, object> analistaArlB = AnalistaArlA();
        analistaArlB["tenant_id"] = ArlB;

        HttpResponseMessage response = await ListarAsync(analistaArlB, caso);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WithoutThePermission_ShouldAnswer403()
    {
        Guid caso = await CasoRemitidoAsync(factory);
        Dictionary<string, object> sinPermiso = AnalistaArlA();
        sinPermiso["permissions"] = new[] { "casos:read" };

        HttpResponseMessage response = await ListarAsync(sinPermiso, caso);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnIntegration_ShouldAnswer403()
    {
        Guid caso = await CasoRemitidoAsync(factory);
        Dictionary<string, object> integracion = new()
        {
            ["sub"] = "01999000-0000-7000-8000-00000000b001",
            ["iss"] = TestAuthenticationHandler.DefaultIssuer,
            ["azp"] = "int-ips-110011234501",
            ["actor_tipo"] = "integracion",
            ["ips_id"] = Ips,
            ["permissions"] = new[] { "incapacidades:read" }
        };

        HttpResponseMessage response = await ListarAsync(integracion, caso);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("Incapacidades.ActorNoAutorizado");
    }
}
