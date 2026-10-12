using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using Halsa.Backend.Infrastructure.Authentication;

namespace Halsa.Backend.FunctionalTests.Identidad;

[Collection(nameof(ApiCollection))]
public sealed class IdentidadEndpointsTests(ApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private HttpClient CreateClient() => factory.CreateClient();

    private static HttpRequestMessage GetYo() =>
        new(HttpMethod.Get, "v1/identidad/yo");

    private static HttpRequestMessage GetYoWithClaims(object claims) =>
        new(HttpMethod.Get, "v1/identidad/yo")
        {
            Headers = { { "X-Test-Claims", JsonSerializer.Serialize(claims) } }
        };

    // ── Actor ARL (predeterminado) ────────────────────────────────────────────

    [Fact]
    public async Task Yo_ShouldReturn200_WithArlContext_ForDefaultTestUser()
    {
        HttpClient client = CreateClient();

        HttpResponseMessage response = await client.SendAsync(GetYo());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonObject? body = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        body.ShouldNotBeNull();

        body["actorTipo"]!.GetValue<string>().ShouldBe("arl");
        body["tenantId"]!.GetValue<string>().ShouldBe("ARL_A");
        body["ipsId"].ShouldBeNull();
        body["sub"].ShouldNotBeNull();
    }

    // ── Actor IPS ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Yo_ShouldReturn200_WithIpsContext_ForIpsActor()
    {
        HttpClient client = CreateClient();

        HttpResponseMessage response = await client.SendAsync(GetYoWithClaims(new
        {
            sub = "01999000-0000-7000-8000-000000000c01",
            email = "medico.ips@halsa.dev",
            iss = TestAuthenticationHandler.DefaultIssuer,
            actor_tipo = "ips",
            ips_id = "110011234501"
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonObject? body = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        body.ShouldNotBeNull();

        body["actorTipo"]!.GetValue<string>().ShouldBe("ips");
        body["tenantId"].ShouldBeNull();
        body["ipsId"]!.GetValue<string>().ShouldBe("110011234501");
    }

    // ── Actor Paciente ────────────────────────────────────────────────────────

    [Fact]
    public async Task Yo_ShouldReturn200_WithPacienteContext_ForPacienteRealm()
    {
        HttpClient client = CreateClient();

        HttpResponseMessage response = await client.SendAsync(GetYoWithClaims(new
        {
            sub = "01999000-0000-7000-8000-000000000d01",
            email = "paciente.dev@halsa.dev",
            iss = TestAuthenticationHandler.PacientesIssuer
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonObject? body = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        body.ShouldNotBeNull();

        body["actorTipo"]!.GetValue<string>().ShouldBe("paciente");
        body["tenantId"].ShouldBeNull();
        body["ipsId"].ShouldBeNull();
    }

    // ── Integración ARL ──────────────────────────────────────────────────────

    [Fact]
    public async Task Yo_ShouldReturn200_WithIntegracionContext_ForServiceAccountArl()
    {
        HttpClient client = CreateClient();

        HttpResponseMessage response = await client.SendAsync(GetYoWithClaims(new
        {
            sub = "service-account-int-arl-ARL_A",
            iss = TestAuthenticationHandler.DefaultIssuer,
            actor_tipo = "integracion",
            tenant_id = "ARL_A"
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonObject? body = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        body.ShouldNotBeNull();

        body["actorTipo"]!.GetValue<string>().ShouldBe("integracion");
        body["tenantId"]!.GetValue<string>().ShouldBe("ARL_A");
        body["ipsId"].ShouldBeNull();
    }

    // ── Claims incompletos → 403 ──────────────────────────────────────────────

    [Fact]
    public async Task Yo_ShouldReturn403_WithClaimsIncompletos_WhenArlHasNoTenantId()
    {
        HttpClient client = CreateClient();

        HttpResponseMessage response = await client.SendAsync(GetYoWithClaims(new
        {
            sub = "01999000-0000-7000-8000-000000000a01",
            iss = TestAuthenticationHandler.DefaultIssuer,
            actor_tipo = "arl"
            // tenant_id ausente
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        JsonObject? body = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        body.ShouldNotBeNull();
        body["errorCode"]!.GetValue<string>().ShouldBe("Tenant.ClaimsIncompletos");
    }

    // ── actor_tipo inválido → 403 ─────────────────────────────────────────────

    [Fact]
    public async Task Yo_ShouldReturn403_WithActorTipoInvalido_WhenPersonalActorHasPacienteClaim()
    {
        HttpClient client = CreateClient();

        HttpResponseMessage response = await client.SendAsync(GetYoWithClaims(new
        {
            sub = "01999000-0000-7000-8000-000000000a01",
            iss = TestAuthenticationHandler.DefaultIssuer,
            actor_tipo = "paciente"
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        JsonObject? body = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        body.ShouldNotBeNull();
        body["errorCode"]!.GetValue<string>().ShouldBe("Tenant.ActorTipoInvalido");
    }

    // ── Sin autenticación → 401 ───────────────────────────────────────────────

    [Fact]
    public async Task Yo_ShouldReturn404_AtOldPath()
    {
        HttpClient client = CreateClient();

        HttpResponseMessage response = await client.SendAsync(new(HttpMethod.Get, "identidad/yo"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Yo_ShouldReturn401_WhenRequestIsUnauthenticated()
    {
        HttpClient client = CreateClient();

        HttpRequestMessage request = new(HttpMethod.Get, "v1/identidad/yo");
        request.Headers.Add("X-Unauthenticated", "true");

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ── /health sin token → 200 (el middleware no bloquea anónimos) ───────────

    [Fact]
    public async Task Health_ShouldReturn200_WhenUnauthenticated()
    {
        HttpClient client = CreateClient();

        HttpRequestMessage request = new(HttpMethod.Get, "health");
        request.Headers.Add("X-Unauthenticated", "true");

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
