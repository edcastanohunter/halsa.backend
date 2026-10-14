using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Halsa.Backend.FunctionalTests.Abstractions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Halsa.Backend.FunctionalTests.OpenApi;

/// <summary>
/// The two sides the contract tests compare: <c>docs/api/atel-api.openapi.yaml</c>, read from the repository, and the
/// document the API generates at <c>/openapi/v1.json</c>.
/// </summary>
internal static partial class Contrato
{
    public static readonly string Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "api", "atel-api.openapi.yaml"));

    public static async Task<JsonObject> GeneradoAsync(ApiFactory factory)
    {
        OpenApiDocument documento = await factory.Services
            .GetRequiredKeyedService<IOpenApiDocumentProvider>("v1")
            .GetOpenApiDocumentAsync();

        string json = await documento.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1);
        return JsonNode.Parse(json)!.AsObject();
    }

    public static string Ref(JsonNode schema) =>
        schema["$ref"]!.GetValue<string>().Replace("#/components/schemas/", string.Empty, StringComparison.Ordinal);

    /// <summary>The property names of <c>components.schemas.{esquema}</c> in the generated document.</summary>
    public static string[] PropiedadesGeneradas(JsonObject documento, string esquema) =>
        [.. documento["components"]!["schemas"]![esquema]!["properties"]!.AsObject().Select(p => p.Key)];

    /// <summary>
    /// The property names of <c>components.schemas.{esquema}.properties</c> in the YAML: the keys one level below
    /// <c>properties:</c>. Enough for this file's layout, without a YAML parser.
    /// </summary>
    public static string[] PropiedadesDelContrato(string esquema)
    {
        string[] lineas = File.ReadAllLines(Path);

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
