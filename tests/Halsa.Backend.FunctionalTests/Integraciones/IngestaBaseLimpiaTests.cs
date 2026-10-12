using System.Net;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Integraciones;

/// <summary>
/// The SPEC 05 criteria that need a clean database: the first radications of <c>ARL_A</c> and <c>ARL_B</c>.
/// This collection has its own <see cref="ApiFactory"/>, so its own container, and nothing else radicates in it.
/// </summary>
[CollectionDefinition(nameof(IngestaBaseLimpiaCollection))]
public sealed class IngestaBaseLimpiaCollection : ICollectionFixture<ApiFactory>;

[Collection(nameof(IngestaBaseLimpiaCollection))]
public sealed class IngestaBaseLimpiaTests(ApiFactory factory)
{
    [Fact]
    public async Task FuratCaidaAltura_FromArlA_ShouldBeTheFirstRadicado_AndArlBShouldStartItsOwn()
    {
        int anio = IntegracionesEndpointsTests.AnioColombia();

        HttpResponseMessage deA = await PostAsync(IntegracionArl(factory, "ARL_A"), "v1/integraciones/furat", Furat());

        deA.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonObject body = await BodyAsync(deA);
        body["radicado"]!.GetValue<string>().ShouldBe($"ATEL-ARL_A-{anio}-000001");
        body["estado"]!.GetValue<string>().ShouldBe("RADICADO");
        deA.Headers.Location!.ToString().ShouldBe($"/v1/integraciones/furat/{body["solicitudId"]!.GetValue<Guid>()}");

        // The same report number in another tenant is another report, with its own sequence.
        HttpResponseMessage deB = await PostAsync(IntegracionArl(factory, "ARL_B"), "v1/integraciones/furat", Furat());

        deB.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await BodyAsync(deB))["radicado"]!.GetValue<string>().ShouldBe($"ATEL-ARL_B-{anio}-000001");
    }
}
