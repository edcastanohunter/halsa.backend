using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Halsa.Backend.FunctionalTests.Abstractions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Halsa.Backend.FunctionalTests.OpenApi;

/// <summary>
/// SPEC 06, step 6: <c>/openapi/v1.json</c> has the two new routes, and their schemas match the ones in
/// <c>docs/api/atel-api.openapi.yaml</c>. The generated names are the C# ones, so the test maps them; the YAML is
/// read from the repository, not copied here.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed partial class ContratoConceptosMedicosTests(ApiFactory factory)
{
    private static readonly string ContratoPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "api", "atel-api.openapi.yaml"));

    /// <summary>Contract schema → generated schema.</summary>
    public static TheoryData<string, string> Esquemas => new()
    {
        { "ConceptoMedicoRequest", "ConceptoMedicoV1" },
        { "RegistroClinicoRequest", "RegistroClinicoRequest" },
        { "ContenidoClinico", "ContenidoClinicoRequest" },
        { "RegistroClinicoCreadoResponse", "RegistroClinicoCreadoResponse" }
    };

    [Theory]
    [InlineData("/v1/integraciones/conceptos-medicos", "ConceptoMedicoV1")]
    [InlineData("/v1/casos/{casoId}/registros-clinicos", "RegistroClinicoRequest")]
    public async Task TheGeneratedDocument_ShouldExposeTheRoute_WithItsRequestAnd201Schemas(string ruta, string request)
    {
        JsonObject documento = await GeneradoAsync();

        JsonObject post = documento["paths"]![ruta]!["post"]!.AsObject();

        Ref(post["requestBody"]!["content"]!["application/json"]!["schema"]!).ShouldBe(request);
        Ref(post["responses"]!["201"]!["content"]!["application/json"]!["schema"]!)
            .ShouldBe("RegistroClinicoCreadoResponse");
        post["responses"]!.AsObject().Select(r => r.Key)
            .ShouldBe(["201", "400", "401", "403", "404", "409"], ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(Esquemas))]
    public async Task TheGeneratedSchema_ShouldHaveTheContractsProperties(string contrato, string generado)
    {
        JsonObject documento = await GeneradoAsync();

        string[] propiedades = [.. documento["components"]!["schemas"]![generado]!["properties"]!.AsObject()
            .Select(p => p.Key)];

        propiedades.ShouldBe(PropiedadesDelContrato(contrato), ignoreOrder: true);
    }

    [Fact]
    public void TheContract_ShouldDeclareBothRoutes()
    {
        string[] lineas = File.ReadAllLines(ContratoPath);

        lineas.ShouldContain("  /integraciones/conceptos-medicos:");
        lineas.ShouldContain("  /casos/{casoId}/registros-clinicos:");
    }

    private async Task<JsonObject> GeneradoAsync()
    {
        OpenApiDocument documento = await factory.Services
            .GetRequiredKeyedService<IOpenApiDocumentProvider>("v1")
            .GetOpenApiDocumentAsync();

        string json = await documento.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1);
        return JsonNode.Parse(json)!.AsObject();
    }

    private static string Ref(JsonNode schema) =>
        schema["$ref"]!.GetValue<string>().Replace("#/components/schemas/", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// The property names of <c>components.schemas.{esquema}.properties</c> in the YAML: the keys one level below
    /// <c>properties:</c>. Enough for this file's layout, without a YAML parser.
    /// </summary>
    private static string[] PropiedadesDelContrato(string esquema)
    {
        string[] lineas = File.ReadAllLines(ContratoPath);

        int inicio = Array.IndexOf(lineas, $"    {esquema}:");
        inicio.ShouldBeGreaterThan(0, $"components.schemas.{esquema} is not in the contract");

        int propiedades = Array.FindIndex(lineas, inicio, l => l == "      properties:");
        var nombres = new List<string>();

        foreach (string linea in lineas.Skip(propiedades + 1))
        {
            if (linea.Length > 0 && !linea.StartsWith("        ", StringComparison.Ordinal))
            {
                break;
            }

            Match clave = Clave().Match(linea);
            if (clave.Success)
            {
                nombres.Add(clave.Groups[1].Value);
            }
        }

        return [.. nombres];
    }

    [GeneratedRegex("^        ([A-Za-z]+):")]
    private static partial Regex Clave();
}
