using System.Net;
using Halsa.Backend.FunctionalTests.Abstractions;

namespace Halsa.Backend.FunctionalTests.Proceso;

/// <summary>SPEC 13, paso 1: <c>/health/vivo</c> es la sonda pública del monitor externo, sin detalle.</summary>
[Collection(nameof(ApiCollection))]
public sealed class HealthVivoTests(ApiFactory factory)
{
    [Fact]
    public async Task Vivo_ShouldAnswer200WithoutDetail_EvenWhenDegraded()
    {
        // The antivirus is Degraded in the tests (nothing listens on its port): still alive.
        HttpResponseMessage response = await factory.CreateClient().GetAsync("health/vivo");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Health_ShouldKeepTheDetailedReport()
    {
        HttpResponseMessage response = await factory.CreateClient().GetAsync("health");

        (await response.Content.ReadAsStringAsync()).ShouldContain("antivirus");
    }
}
