using System.Net;
using Halsa.Backend.FunctionalTests.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Halsa.Backend.FunctionalTests.Proceso;

/// <summary>SPEC 09, paso 1: el Worker es la misma imagen, pero solo responde <c>/health</c>.</summary>
[Collection(nameof(ApiCollection))]
public sealed class RolProcesoEndpointsTests(ApiFactory factory)
{
    [Fact]
    public async Task Worker_ShouldAnswer404_OnBusinessEndpoints()
    {
        await using WebApplicationFactory<Program> worker = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Halsa:Rol", "Worker"));

        HttpResponseMessage response = await worker.CreateClient().GetAsync("v1/casos");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Worker_ShouldStillExposeHealth()
    {
        await using WebApplicationFactory<Program> worker = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Halsa:Rol", "Worker"));

        HttpResponseMessage response = await worker.CreateClient().GetAsync("health");

        // Degraded (no clamd in the tests) still answers 200; what matters is that the route exists.
        response.StatusCode.ShouldNotBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Api_ShouldServeBusinessEndpoints()
    {
        await using WebApplicationFactory<Program> api = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Halsa:Rol", "Api"));

        HttpResponseMessage response = await api.CreateClient().GetAsync("v1/casos");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
