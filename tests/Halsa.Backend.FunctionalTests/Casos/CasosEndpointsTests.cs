using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.FunctionalTests.Abstractions;
using Halsa.Backend.Infrastructure.Authentication;

namespace Halsa.Backend.FunctionalTests.Casos;

[Collection(nameof(ApiCollection))]
public sealed class CasosEndpointsTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    // ── IDs que coinciden con casos-dev.sql ──────────────────────────────────
    private static readonly Guid CasoSura1Id = Guid.Parse("00000000-0000-7000-8001-000000000001");
    private static readonly Guid CasoSura2Id = Guid.Parse("00000000-0000-7000-8001-000000000002");
    private static readonly Guid CasoPositivaId = Guid.Parse("00000000-0000-7000-8001-000000000003");
    private static readonly Guid RemisionSura1Id = Guid.Parse("00000000-0000-7000-8002-000000000001");

    // ── Helpers ───────────────────────────────────────────────────────────────

    private HttpClient CreateClient() => factory.CreateClient();

    /// <summary>
    /// Crea un cliente con claims de analista ARL para el tenant indicado.
    /// Por defecto usa "ARL_A" (tenant_id del TestAuthenticationHandler por defecto).
    /// </summary>
    private HttpClient ArlClient(string tenantId = "ARL_A") =>
        factory.CreateClientWithHeaders(new Dictionary<string, string>
        {
            ["X-Test-Claims"] = JsonSerializer.Serialize(new
            {
                sub = TestAuthenticationHandler.TestUserId.ToString(),
                email = "analista@halsa.dev",
                iss = TestAuthenticationHandler.DefaultIssuer,
                actor_tipo = "arl",
                tenant_id = tenantId,
                permissions = new[] { "casos:read", "casos:write" }
            })
        });

    /// <summary>
    /// El cliente por defecto del factory ya tiene permisos ARL_A — sólo un alias legible.
    /// </summary>
    private HttpClient DefaultArlClient() => CreateClient();

    // ── GET /v1/casos ────────────────────────────────────────────────────────

    [Fact]
    public async Task ListCasos_ShouldReturn200_WithItems_ForArlActorWithCasos()
    {
        HttpClient client = DefaultArlClient();

        HttpResponseMessage response = await client.GetAsync("v1/casos");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonObject? body = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        body.ShouldNotBeNull();
        body["items"].ShouldNotBeNull();
        body["totalCount"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ListCasos_ShouldReturn401_WhenUnauthenticated()
    {
        HttpClient client = CreateClient();

        HttpRequestMessage request = new(HttpMethod.Get, "v1/casos");
        request.Headers.Add("X-Unauthenticated", "true");

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListCasos_ShouldReturn200WithPagination_WhenPageSizeIsSpecified()
    {
        HttpClient client = DefaultArlClient();

        HttpResponseMessage response = await client.GetAsync("v1/casos?page=1&pageSize=1");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonObject? body = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        body.ShouldNotBeNull();

        var items = body["items"]?.AsArray();
        items.ShouldNotBeNull();
        items.Count.ShouldBeLessThanOrEqualTo(1);
    }

    // ── GET /v1/casos/{id} ───────────────────────────────────────────────────

    [Fact]
    public async Task GetCasoById_ShouldReturn200_WhenCasoExistsForArl()
    {
        // El default test user tiene tenant_id = ARL_A; los casos dev usan "arl-sura"
        // Ajustamos a un tenant que coincida con el seed.
        HttpClient client = ArlClient("arl-sura");

        HttpResponseMessage response = await client.GetAsync($"v1/casos/{CasoSura1Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonObject? body = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        body.ShouldNotBeNull();
        body["id"]!.GetValue<string>().ShouldBe(CasoSura1Id.ToString());
        body["radicado"]!.GetValue<string>().ShouldBe("ARL-SURA-2026-001");
        body["tenantId"]!.GetValue<string>().ShouldBe("arl-sura");
    }

    [Fact]
    public async Task GetCasoById_ShouldReturn404_WhenCasoDoesNotExistForArl()
    {
        HttpClient client = DefaultArlClient(); // tenant ARL_A ≠ arl-sura

        HttpResponseMessage response = await client.GetAsync($"v1/casos/{CasoSura1Id}");

        // RLS + query filter ocultan el caso — handler retorna NotFound
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetCasoById_ShouldReturn404_ForNonExistentId()
    {
        HttpClient client = DefaultArlClient();

        HttpResponseMessage response = await client.GetAsync($"v1/casos/{Guid.NewGuid()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetCasoById_ShouldIncludeRemisionesAndAsignaciones_WhenPresent()
    {
        HttpClient client = ArlClient("arl-sura");

        HttpResponseMessage response = await client.GetAsync($"v1/casos/{CasoSura1Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonObject? body = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        body.ShouldNotBeNull();

        var remisiones = body["remisiones"]?.AsArray();
        remisiones.ShouldNotBeNull();
        remisiones.Count.ShouldBe(1);

        var asignaciones = body["asignaciones"]?.AsArray();
        asignaciones.ShouldNotBeNull();
        asignaciones.Count.ShouldBe(1);
    }

    // ── POST /v1/casos (Radicar) ─────────────────────────────────────────────

    [Fact]
    public async Task RadicarCaso_ShouldReturn201_WithLocationHeader_ForArlActor()
    {
        HttpClient client = ArlClient("arl-sura");

        var payload = new
        {
            radicado = $"ARL-SURA-TEST-{Guid.NewGuid():N}",
            tipoEvento = (int)TipoEvento.AccidenteTrabajo,
            trabajadorTipo = "CC",
            trabajadorNumero = "11223344",
            empleadorTipo = "NIT",
            empleadorNumero = "9009009001",
            fechaEvento = DateTimeOffset.UtcNow.AddDays(-1),
            municipioDivipola = "11001",
            esGraveOMortal = false
        };

        HttpResponseMessage response = await client.PostAsJsonAsync("v1/casos", payload);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location.ToString().ShouldStartWith("/v1/casos/");

        JsonObject? body = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        body.ShouldNotBeNull();
        body["id"].ShouldNotBeNull();
    }

    [Fact]
    public async Task RadicarCaso_ShouldReturn401_WhenUnauthenticated()
    {
        HttpClient client = CreateClient();

        var payload = new
        {
            radicado = "TEST-UNAUTH",
            tipoEvento = (int)TipoEvento.AccidenteTrabajo,
            trabajadorTipo = "CC",
            trabajadorNumero = "11223344",
            empleadorTipo = "NIT",
            empleadorNumero = "9009009001",
            fechaEvento = DateTimeOffset.UtcNow.AddDays(-1),
            municipioDivipola = "11001",
            esGraveOMortal = false
        };

        HttpRequestMessage request = new(HttpMethod.Post, "v1/casos");
        request.Content = JsonContent.Create(payload);
        request.Headers.Add("X-Unauthenticated", "true");

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RadicarCaso_ShouldReturn403_ForIpsActor()
    {
        // IPS no tiene tenant_id → TenantId será null → command handler retorna Forbidden
        HttpClient client = factory.CreateClientWithHeaders(new Dictionary<string, string>
        {
            ["X-Test-Claims"] = JsonSerializer.Serialize(new
            {
                sub = "01999000-0000-7000-8000-000000000c01",
                email = "medico@ips.dev",
                iss = TestAuthenticationHandler.DefaultIssuer,
                actor_tipo = "ips",
                ips_id = "110011234501",
                permissions = new[] { "casos:read", "casos:write" }
            })
        });

        var payload = new
        {
            radicado = "TEST-IPS-RADICAR",
            tipoEvento = (int)TipoEvento.AccidenteTrabajo,
            trabajadorTipo = "CC",
            trabajadorNumero = "11223344",
            empleadorTipo = "NIT",
            empleadorNumero = "9009009001",
            fechaEvento = DateTimeOffset.UtcNow.AddDays(-1),
            municipioDivipola = "11001",
            esGraveOMortal = false
        };

        HttpResponseMessage response = await client.PostAsJsonAsync("v1/casos", payload);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ── POST /v1/casos/{id}/remisiones (Remitir) ─────────────────────────────

    [Fact]
    public async Task RemitirCaso_ShouldReturn201_WhenCasoExistsAndIpsEsNueva()
    {
        HttpClient client = ArlClient("arl-sura");

        // Primero crear un caso nuevo para tener control total
        string radicado = $"ARL-SURA-REMITIR-{Guid.NewGuid():N}";
        var casoPayload = new
        {
            radicado,
            tipoEvento = (int)TipoEvento.AccidenteTrabajo,
            trabajadorTipo = "CC",
            trabajadorNumero = "55667788",
            empleadorTipo = "NIT",
            empleadorNumero = "9001230001",
            fechaEvento = DateTimeOffset.UtcNow.AddDays(-2),
            municipioDivipola = "05001",
            esGraveOMortal = false
        };

        HttpResponseMessage createResponse = await client.PostAsJsonAsync("v1/casos", casoPayload);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        JsonObject? createBody = await createResponse.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        string casoId = createBody!["id"]!.GetValue<string>();

        // Remitir
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"v1/casos/{casoId}/remisiones",
            new { ipsId = "990099900001" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location.ShouldNotBeNull();
    }

    [Fact]
    public async Task RemitirCaso_ShouldReturn404_WhenCasoNoExiste()
    {
        HttpClient client = ArlClient("arl-sura");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"v1/casos/{Guid.NewGuid()}/remisiones",
            new { ipsId = "110011234501" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RemitirCaso_ShouldReturn409_WhenYaExisteRemisionVigenteAlaMismaIps()
    {
        HttpClient client = ArlClient("arl-sura");

        // Caso 1 de seed ya tiene una remisión vigente a "110011234501"
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"v1/casos/{CasoSura1Id}/remisiones",
            new { ipsId = "110011234501" });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ── DELETE /v1/casos/{id}/remisiones/{remisionId} (Revocar) ─────────────

    [Fact]
    public async Task RevocarRemision_ShouldReturn204_WhenRemisionVigenteExiste()
    {
        // Crear un caso + remisión frescos para poder revocarla sin afectar otros tests
        HttpClient client = ArlClient("arl-sura");

        string radicado = $"ARL-SURA-REVOCAR-{Guid.NewGuid():N}";
        var casoPayload = new
        {
            radicado,
            tipoEvento = (int)TipoEvento.AccidenteTrabajo,
            trabajadorTipo = "CC",
            trabajadorNumero = "99887766",
            empleadorTipo = "NIT",
            empleadorNumero = "9002340001",
            fechaEvento = DateTimeOffset.UtcNow.AddDays(-3),
            municipioDivipola = "76001",
            esGraveOMortal = false
        };

        HttpResponseMessage createCaso = await client.PostAsJsonAsync("v1/casos", casoPayload);
        createCaso.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonObject? casoBody = await createCaso.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        string casoId = casoBody!["id"]!.GetValue<string>();

        HttpResponseMessage createRemision = await client.PostAsJsonAsync(
            $"v1/casos/{casoId}/remisiones",
            new { ipsId = "770077700001" });
        createRemision.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonObject? remisionBody = await createRemision.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        string remisionId = remisionBody!["id"]!.GetValue<string>();

        // Revocar
        HttpResponseMessage response = await client.DeleteAsync(
            $"v1/casos/{casoId}/remisiones/{remisionId}");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task RevocarRemision_ShouldReturn404_WhenCasoNoExiste()
    {
        HttpClient client = ArlClient("arl-sura");

        HttpResponseMessage response = await client.DeleteAsync(
            $"v1/casos/{Guid.NewGuid()}/remisiones/{Guid.NewGuid()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── PUT /v1/casos/{id}/asignaciones/{sub} (Asignar) ─────────────────────

    [Fact]
    public async Task AsignarCaso_ShouldReturn204_WhenCasoExiste()
    {
        HttpClient client = ArlClient("arl-sura");

        HttpResponseMessage response = await client.PutAsync(
            $"v1/casos/{CasoSura2Id}/asignaciones/sub-medico-nuevo",
            null);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task AsignarCaso_ShouldReturn204_WhenAsignacionYaExiste_Idempotent()
    {
        HttpClient client = ArlClient("arl-sura");

        // Primera asignación
        HttpResponseMessage r1 = await client.PutAsync(
            $"v1/casos/{CasoSura2Id}/asignaciones/sub-idem-test",
            null);
        r1.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Segunda asignación — idempotente
        HttpResponseMessage r2 = await client.PutAsync(
            $"v1/casos/{CasoSura2Id}/asignaciones/sub-idem-test",
            null);
        r2.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task AsignarCaso_ShouldReturn404_WhenCasoNoExiste()
    {
        HttpClient client = ArlClient("arl-sura");

        HttpResponseMessage response = await client.PutAsync(
            $"v1/casos/{Guid.NewGuid()}/asignaciones/sub-nobody",
            null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── DELETE /v1/casos/{id}/asignaciones/{sub} (Desasignar) ───────────────

    [Fact]
    public async Task DesasignarCaso_ShouldReturn204_WhenAsignacionExiste()
    {
        HttpClient client = ArlClient("arl-sura");

        // Asignar primero
        string sub = $"sub-desasignar-{Guid.NewGuid():N}";
        HttpResponseMessage assign = await client.PutAsync(
            $"v1/casos/{CasoSura2Id}/asignaciones/{sub}",
            null);
        assign.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Luego desasignar
        HttpResponseMessage response = await client.DeleteAsync(
            $"v1/casos/{CasoSura2Id}/asignaciones/{sub}");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DesasignarCaso_ShouldReturn404_WhenCasoNoExiste()
    {
        HttpClient client = ArlClient("arl-sura");

        HttpResponseMessage response = await client.DeleteAsync(
            $"v1/casos/{Guid.NewGuid()}/asignaciones/sub-nobody");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DesasignarCaso_ShouldReturn404_WhenSubNoEstaAsignado()
    {
        HttpClient client = ArlClient("arl-sura");

        HttpResponseMessage response = await client.DeleteAsync(
            $"v1/casos/{CasoSura1Id}/asignaciones/sub-inexistente-xyz");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
