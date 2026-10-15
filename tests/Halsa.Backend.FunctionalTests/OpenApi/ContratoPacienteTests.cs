using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;

namespace Halsa.Backend.FunctionalTests.OpenApi;

/// <summary>
/// SPEC 12, step 7: the patient portal's routes, the activation codes and the HC copy requests are in
/// <c>docs/api/atel-api.openapi.yaml</c> and in <c>/openapi/v1.json</c>, with the same response codes, and their schemas
/// have the same properties.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class ContratoPacienteTests(ApiFactory factory)
{
    /// <summary>Route (as in the contract, without <c>/v1</c>), method, and its response codes.</summary>
    public static TheoryData<string, string, string> Rutas => new()
    {
        { "/casos/{casoId}/codigos-activacion", "post", "201,401,403,404,409" },
        { "/paciente/vinculos", "post", "201,400,401,403,409,423,429" },
        { "/paciente/mis-casos", "get", "200,401,403" },
        { "/paciente/resumen", "get", "200,401,403" },
        { "/paciente/casos/{casoId}/linea-tiempo", "get", "200,401,403,404" },
        { "/paciente/incapacidades", "get", "200,401,403" },
        { "/paciente/casos/{casoId}/solicitudes-copia-hc", "post", "201,400,401,403,404,409" },
        { "/paciente/solicitudes-copia-hc", "get", "200,401,403" },
        { "/solicitudes-copia-hc", "get", "200,400,401,403" },
        { "/solicitudes-copia-hc/{id}/tramitar", "post", "204,401,403,404,409" },
        { "/solicitudes-copia-hc/{id}/cerrar", "post", "204,400,401,403,404,409" }
    };

    public static TheoryData<string> Esquemas => new()
    {
        "CodigoActivacionResponse",
        "CanjearCodigoRequest",
        "VinculoCreadoResponse",
        "TrabajadorVinculadoResponse",
        "CasoPacienteResponse",
        "ResumenPacienteResponse",
        "IncapacidadVigenteResponse",
        "ProximoHitoResponse",
        "HitoResponse",
        "IncapacidadPacienteResponse",
        "CrearSolicitudCopiaHcRequest",
        "SolicitudCopiaHcPacienteResponse",
        "SolicitudCopiaHcResponse",
        "CerrarSolicitudCopiaHcRequest"
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
    public async Task TheGeneratedSchema_ShouldHaveTheContractsProperties(string esquema)
    {
        JsonObject documento = await Contrato.GeneradoAsync(factory);

        Contrato.PropiedadesGeneradas(documento, esquema)
            .ShouldBe(Contrato.PropiedadesDelContrato(esquema), ignoreOrder: true);
    }
}
