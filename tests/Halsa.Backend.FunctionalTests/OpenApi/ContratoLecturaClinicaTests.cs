using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;

namespace Halsa.Backend.FunctionalTests.OpenApi;

/// <summary>
/// SPEC 08, step 9: the routes of the clinical reads, the PCL dictamen and the emergency reviews are in
/// <c>docs/api/atel-api.openapi.yaml</c> and in <c>/openapi/v1.json</c>, with the same response codes, and their schemas
/// have the same properties. The generated names are the C# ones, so the test maps them.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class ContratoLecturaClinicaTests(ApiFactory factory)
{
    /// <summary>Route (as in the contract, without <c>/v1</c>), method, and its response codes.</summary>
    public static TheoryData<string, string, string> Rutas => new()
    {
        { "/casos/{casoId}/registros-clinicos", "get", "200,401,403,404" },
        { "/casos/{casoId}/registros-clinicos/{registroId}", "get", "200,401,403,404" },
        { "/casos/{casoId}/incapacidades/{incapacidadId}/soporte", "get", "200,401,403,404,500" },
        { "/casos/{casoId}/dictamen-pcl", "get", "200,401,403,404" },
        { "/casos/{casoId}/dictamen-pcl", "post", "201,400,401,403,404,409" },
        { "/casos/{casoId}/dictamen-pcl/notificar", "post", "204,400,401,403,404,409" },
        { "/auditoria/revisiones-emergencia", "get", "200,400,401,403" },
        { "/auditoria/revisiones-emergencia/{id}/cerrar", "post", "204,400,401,403,404,409" }
    };

    /// <summary>Contract schema → generated schema.</summary>
    public static TheoryData<string, string> Esquemas => new()
    {
        { "RegistroClinicoResponse", "RegistroClinicoResponse" },
        { "ProfesionalFirma", "ProfesionalRef" },
        { "RegistrarDictamenPclRequest", "RegistrarDictamenPclRequest" },
        { "NotificarDictamenPclRequest", "NotificarDictamenPclRequest" },
        { "DictamenPclResponse", "DictamenPclResponse" },
        { "Calificador", "CalificadorResponse" },
        { "RevisionEmergenciaResponse", "RevisionEmergenciaResponse" },
        { "CerrarRevisionEmergenciaRequest", "CerrarRevisionEmergenciaRequest" }
    };

    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task EachRoute_ShouldBeInBothDocuments_WithTheSameResponseCodes(string ruta, string metodo, string codigos)
    {
        // The contract: the path, and the method one level below it.
        string[] lineas = File.ReadAllLines(Contrato.Path);
        int path = Array.IndexOf(lineas, $"  {ruta}:");
        path.ShouldBeGreaterThan(0, $"{ruta} is not in the contract");
        lineas.Skip(path + 1).TakeWhile(l => !l.StartsWith("  /", StringComparison.Ordinal) && l != "webhooks:")
            .ShouldContain($"    {metodo}:");

        JsonObject operacion = (await Contrato.GeneradoAsync(factory))["paths"]![$"/v1{ruta}"]![metodo]!.AsObject();

        operacion["responses"]!.AsObject().Select(r => r.Key).ShouldBe(codigos.Split(','), ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(Esquemas))]
    public async Task TheGeneratedSchema_ShouldHaveTheContractsProperties(string contrato, string generado)
    {
        JsonObject documento = await Contrato.GeneradoAsync(factory);

        Contrato.PropiedadesGeneradas(documento, generado)
            .ShouldBe(Contrato.PropiedadesDelContrato(contrato), ignoreOrder: true);
    }

    [Fact]
    public async Task TheDictamenPost_ShouldTakeAndReturnTheContractsSchemas()
    {
        JsonObject post = (await Contrato.GeneradoAsync(factory))["paths"]!["/v1/casos/{casoId}/dictamen-pcl"]!["post"]!.AsObject();

        Contrato.Ref(post["requestBody"]!["content"]!["application/json"]!["schema"]!).ShouldBe("RegistrarDictamenPclRequest");
        Contrato.Ref(post["responses"]!["201"]!["content"]!["application/json"]!["schema"]!).ShouldBe("DictamenPclResponse");
    }
}
