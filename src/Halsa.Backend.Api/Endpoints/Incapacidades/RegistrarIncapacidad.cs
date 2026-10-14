using System.Security.Claims;
using System.Text;
using Halsa.Backend.Api.Endpoints.Integraciones;
using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Incapacidades;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Halsa.Backend.Api.Endpoints.Incapacidades;

/// <summary>
/// <c>POST /v1/casos/{casoId}/incapacidades</c> — Carga un certificado de incapacidad temporal con su PDF (SPEC 07),
/// desde el backoffice (una persona con MFA reciente, <c>acr=2</c>) o desde la integración de una IPS.
///
/// <c>multipart/form-data</c> con exactamente dos partes: <c>datos</c> (JSON) y <c>soporte</c> (PDF, 10 MB como máximo;
/// 12 MB el request). El endpoint comprueba la política, la llave, las partes, los tamaños y la firma <c>%PDF-</c>; el
/// comando, todo lo demás. Responde <c>201</c> con <c>Location</c>; <c>400</c> (llave, partes o formato de
/// <c>datos</c>), <c>401</c> con step-up, <c>403</c>, <c>404</c>, <c>409</c> (llave con otro cuerpo, certificado
/// repetido), <c>413</c>, <c>415</c>, <c>422</c> (una regla, o un soporte infectado) o <c>503</c> (sin antivirus).
/// </summary>
internal sealed class RegistrarIncapacidad : IEndpoint
{
    public const string ParteDatos = "datos";
    public const string ParteSoporte = "soporte";

    public const long MaxRequest = 12 * 1024 * 1024;
    public const long MaxSoporte = 10 * 1024 * 1024;
    public const long MaxDatos = 64 * 1024;

    private const string NameClaimType = "name";
    private const string ClientIdClaimType = "azp";

    private static readonly byte[] FirmaPdf = Encoding.ASCII.GetBytes("%PDF-");

    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("casos/{casoId:guid}/incapacidades", CargarAsync)
            .WithTags(Tags.Incapacidades)
            .WithName("RegistrarIncapacidad")
            .WithSummary("Carga un certificado de incapacidad temporal con su soporte PDF.")
            .Accepts<IncapacidadRequest>("multipart/form-data")
            .Produces<IncapacidadResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireAuthorization(Policies.IncapacidadEscritura)
            .Auditable(RecursoAuditoria.Incapacidad);

    private static async Task<IResult> CargarAsync(
        Guid casoId,
        HttpContext http,
        ITenantContext tenantContext,
        IProfesionalActual profesionalActual,
        ISender sender,
        CancellationToken ct)
    {
        // 1. A person of an ARL or an IPS, or the integration of an IPS. Visibility of the case is the command's.
        bool integracion = tenantContext.Tipo == ActorTipo.Integracion;
        if (!(tenantContext.Tipo is ActorTipo.Arl or ActorTipo.Ips || integracion && tenantContext.IpsId is not null))
        {
            return Problema(IncapacidadCargaErrors.ActorNoAutorizado);
        }

        // 2. The key, before the body is read.
        string? llave = http.Request.Headers[IntegracionEndpoints.IdempotencyKeyHeader].FirstOrDefault();
        if (!Guid.TryParse(llave, out _))
        {
            return Problema(IntegracionErrors.IdempotencyKeyInvalida);
        }

        ProfesionalDelToken? profesional = null;
        if (!integracion)
        {
            Result<ProfesionalRef> firma = profesionalActual.Obtener();
            if (firma.IsFailure)
            {
                return Problema(firma.Error);
            }

            profesional = new ProfesionalDelToken(firma.Value, http.User.FindFirstValue(NameClaimType));
        }

        // 3 and 4. The parts, their sizes and the PDF signature.
        Result<Partes> partes = await LeerPartesAsync(http.Request, ct);
        if (partes.IsFailure)
        {
            return Problema(partes.Error);
        }

        if (!partes.Value.Soporte.AsSpan().StartsWith(FirmaPdf))
        {
            return Problema(IncapacidadCargaErrors.NoEsPdf);
        }

        Result<IncapacidadResponse> result = await sender.Send(new RegistrarIncapacidadCommand(
            new CasoMedicoId(casoId),
            llave,
            partes.Value.Datos,
            partes.Value.Soporte,
            profesional,
            http.User.FindFirstValue(ClientIdClaimType)), ct);

        return result.IsSuccess
            ? Results.Created($"/v1/casos/{casoId}/incapacidades/{result.Value.Id}", result.Value)
            : IntegracionEndpoints.Problem(result);
    }

    private static IResult Problema(Error error) => IntegracionEndpoints.Problem(Result.Failure(error));

    private sealed record Partes(byte[] Datos, byte[] Soporte);

    /// <summary>
    /// Exactly the two parts, each read once into a bounded buffer. A request over 12 MB is refused by its
    /// <c>Content-Length</c> or, when Kestrel serves it, by the server's own limit; each part by what it carries.
    /// </summary>
    private static async Task<Result<Partes>> LeerPartesAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxRequest)
        {
            return Result.Failure<Partes>(IncapacidadCargaErrors.SoporteDemasiadoGrande);
        }

        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limite)
        {
            limite.MaxRequestBodySize = MaxRequest;
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out MediaTypeHeaderValue? tipo) ||
            !tipo.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase) ||
            HeaderUtilities.RemoveQuotes(tipo.Boundary).Value is not { Length: > 0 } boundary)
        {
            return Result.Failure<Partes>(IncapacidadCargaErrors.PartesInvalidas);
        }

        var reader = new MultipartReader(boundary, request.Body);
        byte[]? datos = null;
        byte[]? soporte = null;

        try
        {
            while (await reader.ReadNextSectionAsync(ct) is { } seccion)
            {
                string? nombre = ContentDispositionHeaderValue.TryParse(seccion.ContentDisposition, out var disposicion)
                    ? HeaderUtilities.RemoveQuotes(disposicion.Name).Value
                    : null;

                switch (nombre)
                {
                    case ParteDatos when datos is null:
                        datos = await LeerAcotadoAsync(seccion.Body, MaxDatos, ct);
                        if (datos is null)
                        {
                            return Result.Failure<Partes>(IncapacidadCargaErrors.SoporteDemasiadoGrande);
                        }

                        break;

                    case ParteSoporte when soporte is null:
                        soporte = await LeerAcotadoAsync(seccion.Body, MaxSoporte, ct);
                        if (soporte is null)
                        {
                            return Result.Failure<Partes>(IncapacidadCargaErrors.SoporteDemasiadoGrande);
                        }

                        break;

                    default:
                        // A third part, a repeated one, or one without a name.
                        return Result.Failure<Partes>(IncapacidadCargaErrors.PartesInvalidas);
                }
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Result.Failure<Partes>(IncapacidadCargaErrors.SoporteDemasiadoGrande);
        }
        catch (IOException)
        {
            // A malformed multipart body (no closing boundary, broken headers).
            return Result.Failure<Partes>(IncapacidadCargaErrors.PartesInvalidas);
        }
        catch (InvalidDataException)
        {
            return Result.Failure<Partes>(IncapacidadCargaErrors.PartesInvalidas);
        }

        return datos is null || soporte is null
            ? Result.Failure<Partes>(IncapacidadCargaErrors.PartesInvalidas)
            : Result.Success(new Partes(datos, soporte));
    }

    /// <summary>The part, or <c>null</c> as soon as it goes past <paramref name="maximo"/>.</summary>
    private static async Task<byte[]?> LeerAcotadoAsync(Stream parte, long maximo, CancellationToken ct)
    {
        using var destino = new MemoryStream();
        byte[] buffer = new byte[81920];

        int leidos;
        while ((leidos = await parte.ReadAsync(buffer, ct)) > 0)
        {
            if (destino.Length + leidos > maximo)
            {
                return null;
            }

            destino.Write(buffer, 0, leidos);
        }

        return destino.ToArray();
    }
}
