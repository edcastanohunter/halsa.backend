using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Api.Middleware;

/// <summary>
/// SPEC 13, tokens ligados a certificado (RFC 8705). Con <see cref="IntegracionesOptions.ExigirCertificado"/>, un actor
/// <see cref="ActorTipo.Integracion"/> debe llegar con el certificado cliente que validó Nginx en
/// <see cref="CabeceraCertificado"/> (PEM url-encoded, <c>$ssl_client_escaped_cert</c>), y su huella
/// <c>base64url(SHA-256(DER))</c> debe ser el <c>cnf.x5t#S256</c> del token. Un token robado no sirve sin la llave del
/// certificado, ni por <c>api.&lt;dominio&gt;</c>, donde no hay mTLS y Nginx borra la cabecera.
///
/// La cabecera solo vale si el request viene del proxy de confianza: <c>UseProxyConfiable</c> la borra de cualquier otro
/// origen. Posición: después de <see cref="TenantContextMiddleware"/>, que resuelve el actor, y antes de la autorización.
/// </summary>
public sealed class IntegrationCertificateBindingMiddleware(RequestDelegate next, IntegracionesOptions options)
{
    public const string CabeceraCertificado = "X-Client-Cert";

    private const string ConfirmationClaimType = "cnf";
    private const string HuellaSha256 = "x5t#S256";

    public static readonly Error CertificadoNoVinculado = Error.Unauthorized(
        "Auth.CertificadoNoVinculado",
        "El token de integración no está ligado al certificado cliente presentado.");

    public async Task InvokeAsync(HttpContext context, ITenantContext tenant)
    {
        if (options.ExigirCertificado
            && tenant.Resuelto
            && tenant.Tipo == ActorTipo.Integracion
            && !Vinculado(context))
        {
            await CustomResults.Problem(Result.Failure(CertificadoNoVinculado)).ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private static bool Vinculado(HttpContext context)
    {
        string? esperada = HuellaDelToken(context.User);
        string? presentada = HuellaDelCertificado(context.Request.Headers[CabeceraCertificado].ToString());

        return esperada is not null
               && presentada is not null
               && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(esperada), Encoding.ASCII.GetBytes(presentada));
    }

    /// <summary><c>cnf.x5t#S256</c> del token; <c>null</c> si el token no está ligado a un certificado.</summary>
    internal static string? HuellaDelToken(ClaimsPrincipal user)
    {
        string? cnf = user.FindFirstValue(ConfirmationClaimType);

        if (string.IsNullOrWhiteSpace(cnf))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(cnf);
            return json.RootElement.ValueKind == JsonValueKind.Object
                   && json.RootElement.TryGetProperty(HuellaSha256, out JsonElement huella)
                   && huella.ValueKind == JsonValueKind.String
                ? huella.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>base64url(SHA-256(DER))</c> del certificado de la cabecera; <c>null</c> si falta o no es un PEM válido.</summary>
    internal static string? HuellaDelCertificado(string cabecera)
    {
        if (string.IsNullOrWhiteSpace(cabecera))
        {
            return null;
        }

        try
        {
            using X509Certificate2 certificado = X509Certificate2.CreateFromPem(Uri.UnescapeDataString(cabecera));
            return Base64Url.EncodeToString(SHA256.HashData(certificado.RawData));
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
