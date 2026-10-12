using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using Halsa.Backend.Tests.Shared;

namespace Halsa.Backend.FunctionalTests.Integraciones;

/// <summary>The SPEC 05 actors as the test handler sees them, and the request helpers.</summary>
internal static class IngestaClientes
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>A client-credentials token of <c>int-arl-{tenant}</c>.</summary>
    public static HttpClient IntegracionArl(ApiFactory factory, string tenant) => ConClaims(factory, new
    {
        sub = "01999000-0000-7000-8000-00000000c001",
        iss = TestAuthenticationHandler.DefaultIssuer,
        azp = $"int-arl-{tenant}",
        actor_tipo = "integracion",
        tenant_id = tenant,
        permissions = new[] { "integracion:furat:write", "integracion:furep:write", "webhooks:manage" }
    });

    /// <summary><c>analista.arla</c>: an ARL person, without the integration permissions.</summary>
    public static HttpClient AnalistaArlA(ApiFactory factory) => ConClaims(factory, new
    {
        sub = "01999000-0000-7000-8000-00000000a001",
        iss = TestAuthenticationHandler.DefaultIssuer,
        azp = "halsa-backoffice",
        actor_tipo = "arl",
        tenant_id = "ARL_A",
        permissions = new[] { "casos:read", "casos:write" }
    });

    /// <summary>A client-credentials token of <c>int-ips-110011234501</c>.</summary>
    public static HttpClient IntegracionIps(ApiFactory factory) => ConClaims(factory, new
    {
        sub = "01999000-0000-7000-8000-00000000b001",
        iss = TestAuthenticationHandler.DefaultIssuer,
        azp = "int-ips-110011234501",
        actor_tipo = "integracion",
        ips_id = "110011234501",
        permissions = new[] { "integracion:clinico:write", "incapacidades:write" }
    });

    private static HttpClient ConClaims(ApiFactory factory, object claims) =>
        factory.CreateClientWithHeaders(new Dictionary<string, string>
        {
            ["X-Test-Claims"] = JsonSerializer.Serialize(claims)
        });

    /// <summary>The <c>FuratCaidaAltura</c> example, with another report number and, optionally, other data.</summary>
    public static JsonObject Furat(
        string? numeroReporteOrigen = null,
        string? trabajadorNumero = null,
        DateTimeOffset? fechaHora = null)
    {
        JsonObject cuerpo = EjemplosContrato.Objeto(EjemplosContrato.FuratCaidaAltura);

        if (numeroReporteOrigen is not null)
        {
            cuerpo["numeroReporteOrigen"] = numeroReporteOrigen;
        }

        if (trabajadorNumero is not null)
        {
            cuerpo["trabajador"]!["identificacion"]!["numero"] = trabajadorNumero;
        }

        if (fechaHora is not null)
        {
            cuerpo["evento"]!["fechaHora"] = fechaHora.Value.ToString("O");
        }

        return cuerpo;
    }

    public static JsonObject Furep(string numeroReporteOrigen)
    {
        JsonObject cuerpo = EjemplosContrato.Objeto(EjemplosContrato.FurepAsma);
        cuerpo["numeroReporteOrigen"] = numeroReporteOrigen;
        return cuerpo;
    }

    public static Task<HttpResponseMessage> PostAsync(
        HttpClient client, string ruta, JsonObject cuerpo, Guid? llave = null, bool sinLlave = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ruta)
        {
            Content = new StringContent(cuerpo.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };

        if (!sinLlave)
        {
            request.Headers.Add("Idempotency-Key", (llave ?? Guid.NewGuid()).ToString());
        }

        return client.SendAsync(request);
    }

    public static async Task<JsonObject> BodyAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions))!;

    /// <summary>An ARL tenant code that no other test uses, so its sequence starts at 1.</summary>
    public static string TenantNuevo(string prefijo) =>
        $"{prefijo}_{Guid.NewGuid():N}"[..20].ToUpperInvariant();
}
