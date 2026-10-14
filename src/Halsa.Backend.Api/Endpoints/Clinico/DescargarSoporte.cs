using System.Security.Claims;
using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Archivos;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Clinico.Queries.GetSoporte;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Incapacidades;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.Net.Http.Headers;

namespace Halsa.Backend.Api.Endpoints.Clinico;

/// <summary>
/// <c>GET /v1/casos/{casoId}/incapacidades/{incapacidadId}/soporte</c> — El PDF original de la incapacidad, descifrado
/// (SPEC 08). Contiene el diagnóstico, así que va con la política <see cref="Policies.ClinicoLectura"/>, como la HC.
///
/// La lectura se audita (<c>LEER / SOPORTE</c>) antes de abrir el archivo. Si el archivo no descifra o su SHA-256 no
/// es el del original, responde <c>500</c> con <c>Archivos.Corrupto</c> y no entrega bytes.
/// </summary>
internal sealed partial class DescargarSoporte : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("casos/{casoId:guid}/incapacidades/{incapacidadId:guid}/soporte", async (
                Guid casoId,
                Guid incapacidadId,
                ClaimsPrincipal user,
                IClinicalAuthorization clinicalAuthorization,
                ISender sender,
                IFileStore archivos,
                HttpContext context,
                ILogger<DescargarSoporte> logger,
                CancellationToken ct) =>
            {
                var caso = new CasoMedicoId(casoId);

                Result acceso = await clinicalAuthorization.AutorizarAsync(
                    user, caso, Policies.ClinicoLectura, AccionAuditoria.Leer, RecursoAuditoria.Soporte, ct);
                if (acceso.IsFailure)
                {
                    return CustomResults.Problem(acceso);
                }

                Result<SoporteDescargable> soporte = await sender.Send(
                    new GetSoporteQuery(caso, new IncapacidadId(incapacidadId)), ct);
                if (soporte.IsFailure)
                {
                    return CustomResults.Problem(soporte);
                }

                Result<byte[]> pdf = await SoporteArchivo.AbrirVerificadoAsync(archivos, soporte.Value, ct);
                if (pdf.IsFailure)
                {
                    LogSoporteCorrupto(logger, soporte.Value.SoporteId, casoId);
                    return CustomResults.Problem(pdf);
                }

                // Set by hand: Results.File would add filename*=, and the contract fixes the exact header.
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.ContentDisposition =
                    $"attachment; filename=\"incapacidad-{soporte.Value.NumeroCertificado}.pdf\"";

                return Results.Bytes(pdf.Value, "application/pdf");
            })
        .WithTags(Tags.Clinico)
        .WithName("DescargarSoporteIncapacidad")
        .WithSummary("Descarga el PDF soporte de la incapacidad (acceso clínico auditado).")
        .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status500InternalServerError)
        .RequireClinicalPolicy(Policies.ClinicoLectura)
        .Auditable(RecursoAuditoria.Soporte);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "El soporte {SoporteId} del caso {CasoId} no pasó la verificación de integridad")]
    private static partial void LogSoporteCorrupto(ILogger logger, Guid soporteId, Guid casoId);
}
