using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.Api.Endpoints.Integraciones.Webhooks;
using Halsa.Backend.Api.Middleware;
using Halsa.Backend.FunctionalTests.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Halsa.Backend.FunctionalTests.Integraciones;

/// <summary>
/// SPEC 13, paso 2: con <c>Integraciones:ExigirCertificado</c>, el token de integración solo vale con el certificado al
/// que está ligado (<c>cnf.x5t#S256</c>), y la cabecera solo cuenta si la escribió el proxy de confianza.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class CertificadoVinculadoTests(ApiFactory factory)
{
    private const string Proxy = "10.40.0.2";
    private const string Ruta = $"v1/{SuscripcionesWebhookRutas.Base}";

    [Fact]
    public async Task MatchingCertificate_ShouldBeServed()
    {
        using X509Certificate2 certificado = Certificado("int-arl-ARL_A");

        HttpResponseMessage response = await GetAsync(Huella(certificado), certificado, desde: Proxy);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnotherCertificate_ShouldBe401CertificadoNoVinculado()
    {
        using X509Certificate2 ligado = Certificado("int-arl-ARL_A");
        using X509Certificate2 otro = Certificado("int-ips-110011234501");

        HttpResponseMessage response = await GetAsync(Huella(ligado), otro, desde: Proxy);

        await DebeSerNoVinculadoAsync(response);
    }

    [Fact]
    public async Task MissingCertificate_ShouldBe401CertificadoNoVinculado()
    {
        using X509Certificate2 ligado = Certificado("int-arl-ARL_A");

        HttpResponseMessage response = await GetAsync(Huella(ligado), certificado: null, desde: Proxy);

        await DebeSerNoVinculadoAsync(response);
    }

    [Fact]
    public async Task TokenWithoutCnf_ShouldBe401CertificadoNoVinculado()
    {
        using X509Certificate2 certificado = Certificado("int-arl-ARL_A");

        HttpResponseMessage response = await GetAsync(huella: null, certificado, desde: Proxy);

        await DebeSerNoVinculadoAsync(response);
    }

    [Fact]
    public async Task CertificateHeader_FromOutsideTheTrustedProxy_ShouldBeIgnored()
    {
        using X509Certificate2 certificado = Certificado("int-arl-ARL_A");

        HttpResponseMessage response = await GetAsync(Huella(certificado), certificado, desde: "198.51.100.9");

        await DebeSerNoVinculadoAsync(response);
    }

    [Fact]
    public async Task WithoutTheOption_TheCertificateShouldNotBeRequired()
    {
        HttpResponseMessage response = await GetAsync(huella: null, certificado: null, desde: Proxy, exigir: false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> GetAsync(
        string? huella, X509Certificate2? certificado, string desde, bool exigir = true)
    {
        await using WebApplicationFactory<Program> api = factory.WithWebHostBuilder(builder => builder
            .UseSetting("Integraciones:ExigirCertificado", exigir ? "true" : "false")
            .UseSetting("Red:ProxyConfiable", "10.40.0.0/24"));

        var claims = new Dictionary<string, object>
        {
            ["sub"] = "01999000-0000-7000-8000-00000000c001",
            ["iss"] = TestAuthenticationHandler.DefaultIssuer,
            ["azp"] = "int-arl-ARL_A",
            ["actor_tipo"] = "integracion",
            ["tenant_id"] = "ARL_A",
            ["permissions"] = new[] { "webhooks:manage" }
        };

        if (huella is not null)
        {
            claims["cnf"] = new Dictionary<string, string> { ["x5t#S256"] = huella };
        }

        HttpClient client = api.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Claims", JsonSerializer.Serialize(claims));
        client.DefaultRequestHeaders.Add(RemoteIpDePrueba.Cabecera, desde);

        if (certificado is not null)
        {
            // What Nginx sends: $ssl_client_escaped_cert, the PEM url-encoded.
            client.DefaultRequestHeaders.Add(
                IntegrationCertificateBindingMiddleware.CabeceraCertificado,
                Uri.EscapeDataString(certificado.ExportCertificatePem()));
        }

        return await client.GetAsync(Ruta);
    }

    private static async Task DebeSerNoVinculadoAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        JsonObject problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        problem["errorCode"]!.GetValue<string>().ShouldBe("Auth.CertificadoNoVinculado");
    }

    /// <summary>A client certificate like the ones the integrations CA issues: ECDSA P-256, CN = client id.</summary>
    private static X509Certificate2 Certificado(string clientId)
    {
        using var llave = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var solicitud = new CertificateRequest($"CN={clientId}", llave, HashAlgorithmName.SHA256);
        solicitud.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], critical: false));

        return solicitud.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    /// <summary>The <c>x5t#S256</c> Keycloak puts in <c>cnf</c>: base64url(SHA-256(DER)).</summary>
    private static string Huella(X509Certificate2 certificado) =>
        Base64Url.EncodeToString(SHA256.HashData(certificado.RawData));
}
