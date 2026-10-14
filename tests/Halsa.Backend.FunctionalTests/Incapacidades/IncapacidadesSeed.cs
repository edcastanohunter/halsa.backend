using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;

namespace Halsa.Backend.FunctionalTests.Incapacidades;

/// <summary>Actors, bodies and the multipart request of the SPEC 07 tests.</summary>
internal static class IncapacidadesSeed
{
    public const string TextoDelPdf = "Certificado de incapacidad - texto de prueba";

    public static readonly byte[] Pdf = Encoding.ASCII.GetBytes($"%PDF-1.7\nBT ({TextoDelPdf}) ET\n%%EOF\n");

    /// <summary>Today in Colombia: an incapacidad cannot start later.</summary>
    public static DateOnly Hoy => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-5)).DateTime);

    public static Dictionary<string, object> ConAcr(Dictionary<string, object> actor, string acr)
    {
        actor["acr"] = acr;
        return actor;
    }

    /// <summary>The <c>datos</c> part, with the contract's fields.</summary>
    public static JsonObject Datos(
        DateOnly? fechaInicio = null,
        int dias = 10,
        string tipo = "INICIAL",
        Guid? prorrogaDe = null,
        string codigo = "S62.6",
        string? numeroCertificado = null,
        bool conProfesional = true) => new()
    {
        ["numeroCertificado"] = numeroCertificado ?? $"CERT-{Guid.NewGuid():N}"[..30],
        ["tipo"] = tipo,
        ["prorrogaDe"] = prorrogaDe?.ToString(),
        ["fechaInicio"] = (fechaInicio ?? Hoy.AddDays(-20)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["dias"] = dias,
        ["diagnosticoPrincipal"] = new JsonObject
        {
            ["sistema"] = "CIE-10", ["codigo"] = codigo, ["descripcion"] = "Fractura de otro dedo de la mano"
        },
        ["profesional"] = conProfesional
            ? new JsonObject
            {
                ["identificacion"] = new JsonObject { ["tipo"] = "CC", ["numero"] = "79111222" },
                ["nombreCompleto"] = "Ana Ruiz",
                ["registroRethus"] = "RM-12345"
            }
            : null,
        ["ipsEmisora"] = new JsonObject { ["codigoReps"] = Ips, ["nombre"] = "IPS Uno" },
        ["modalidad"] = "PRESENCIAL",
        ["observaciones"] = "Reposo, observacion confidencial."
    };

    public static string Ruta(Guid caso) => $"v1/casos/{caso}/incapacidades";

    /// <summary>The upload: <c>datos</c> and <c>soporte</c>, or other parts when a test needs them.</summary>
    public static Task<HttpResponseMessage> CargarAsync(
        HttpClient client,
        Guid caso,
        JsonObject? datos = null,
        byte[]? soporte = null,
        Guid? llave = null,
        bool sinLlave = false,
        Action<MultipartFormDataContent>? partesExtra = null,
        bool sinSoporte = false)
    {
        var cuerpo = new MultipartFormDataContent();

        var json = new StringContent((datos ?? Datos()).ToJsonString(), Encoding.UTF8);
        json.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        cuerpo.Add(json, "datos");

        if (!sinSoporte)
        {
            var pdf = new ByteArrayContent(soporte ?? Pdf);
            pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            cuerpo.Add(pdf, "soporte", "certificado.pdf");
        }

        partesExtra?.Invoke(cuerpo);

        var request = new HttpRequestMessage(HttpMethod.Post, Ruta(caso)) { Content = cuerpo };
        if (!sinLlave)
        {
            request.Headers.Add("Idempotency-Key", (llave ?? Guid.NewGuid()).ToString());
        }

        return client.SendAsync(request);
    }

    /// <summary>A case of <paramref name="tenant"/> whose event was long ago, remitted to the seed IPS.</summary>
    public static Task<Guid> CasoRemitidoAsync(ApiFactory factory, string tenant = ArlA, int diasDesdeElEvento = 400) =>
        SeedCasoAsync(factory, tenant, remitirA: Ips, fechaEvento: DateTimeOffset.UtcNow.AddDays(-diasDesdeElEvento));

    public static int ArchivosEnDisco(ApiFactory factory, string tenant) =>
        Directory.Exists(Path.Combine(factory.ArchivosRaiz, tenant))
            ? Directory.GetFiles(Path.Combine(factory.ArchivosRaiz, tenant), "*.bin", SearchOption.AllDirectories).Length
            : 0;
}
