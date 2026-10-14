using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;

namespace Halsa.Backend.FunctionalTests.OpenApi;

/// <summary>
/// SPEC 09, paso 9: las rutas de plazos están en <c>docs/api/atel-api.openapi.yaml</c> y en <c>/openapi/v1.json</c>, con
/// los mismos códigos de respuesta, y sus esquemas tienen las mismas propiedades.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class ContratoPlazosTests(ApiFactory factory)
{
    /// <summary>Route (as in the contract, without <c>/v1</c>), method, and its response codes.</summary>
    public static TheoryData<string, string, string> Rutas => new()
    {
        { "/casos/{casoId}/plazos", "get", "200,401,403,404" },
        { "/plazos", "get", "200,400,401,403" },
        { "/casos/{casoId}/plazos/{plazoId}/cumplir", "post", "204,400,401,403,404,409" }
    };

    /// <summary>Contract schema → generated schema.</summary>
    public static TheoryData<string, string> Esquemas => new()
    {
        { "PlazoResponse", "PlazoResponse" },
        { "CumplirPlazoRequest", "CumplirPlazoRequest" },
        { "PlazoResumen", "PlazoResumen" }
    };

    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task EachRoute_ShouldBeInBothDocuments_WithTheSameResponseCodes(string ruta, string metodo, string codigos)
    {
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
}
