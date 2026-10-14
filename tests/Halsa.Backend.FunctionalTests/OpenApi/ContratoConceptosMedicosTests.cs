using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;

namespace Halsa.Backend.FunctionalTests.OpenApi;

/// <summary>
/// SPEC 06, step 6: <c>/openapi/v1.json</c> has the two new routes, and their schemas match the ones in
/// <c>docs/api/atel-api.openapi.yaml</c>. The generated names are the C# ones, so the test maps them; the YAML is
/// read from the repository, not copied here.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class ContratoConceptosMedicosTests(ApiFactory factory)
{
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
        JsonObject documento = await Contrato.GeneradoAsync(factory);

        JsonObject post = documento["paths"]![ruta]!["post"]!.AsObject();

        Contrato.Ref(post["requestBody"]!["content"]!["application/json"]!["schema"]!).ShouldBe(request);
        Contrato.Ref(post["responses"]!["201"]!["content"]!["application/json"]!["schema"]!)
            .ShouldBe("RegistroClinicoCreadoResponse");
        post["responses"]!.AsObject().Select(r => r.Key)
            .ShouldBe(["201", "400", "401", "403", "404", "409"], ignoreOrder: true);
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
    public void TheContract_ShouldDeclareBothRoutes()
    {
        string[] lineas = File.ReadAllLines(Contrato.Path);

        lineas.ShouldContain("  /integraciones/conceptos-medicos:");
        lineas.ShouldContain("  /casos/{casoId}/registros-clinicos:");
    }
}
