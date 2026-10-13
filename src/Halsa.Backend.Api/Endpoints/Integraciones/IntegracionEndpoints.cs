using System.Security.Claims;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Application.Integraciones.Canonico;
using Halsa.Backend.Application.Integraciones.Commands.Radicar;
using Halsa.Backend.Application.Integraciones.Conceptos;
using Halsa.Backend.Application.Integraciones.Queries.ConsultarRadicacion;
using Halsa.Backend.Infrastructure.Http;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Api.Endpoints.Integraciones;

/// <summary>
/// Lo común a los endpoints de integración: leer el cuerpo crudo, la cabecera de idempotencia, y traducir el
/// resultado a la respuesta o al problema del contrato. La radicación del FURAT y del FUREP responde <c>202</c>;
/// el concepto médico de una IPS (SPEC 06), <c>201</c>.
/// </summary>
internal static class IntegracionEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    private const string ClientIdClaimType = "azp";

    /// <summary>
    /// El cuerpo se lee crudo, sin model binding: su SHA-256 es la huella de la idempotencia y el propio cuerpo,
    /// cifrado, la evidencia de lo recibido. Lo valida el adaptador del formato, con errores por ruta JSON.
    /// </summary>
    public static async Task<IResult> RadicarAsync(
        TipoReporte tipo,
        string ruta,
        HttpContext http,
        ISender sender,
        CancellationToken ct)
    {
        using var cuerpo = new MemoryStream();
        await http.Request.Body.CopyToAsync(cuerpo, ct);

        Result<RadicacionResponse> result = await sender.Send(new RadicarReporteCommand(
            tipo,
            http.Request.Headers[IdempotencyKeyHeader].FirstOrDefault(),
            cuerpo.ToArray(),
            http.User.FindFirstValue(ClientIdClaimType),
            CorrelationId.From(http)), ct);

        return result.IsSuccess
            ? Results.Accepted($"/v1/{ruta}/{result.Value.SolicitudId}", result.Value)
            : Problem(result);
    }

    /// <summary>El cuerpo crudo, como en la radicación: su SHA-256 es la huella de la idempotencia.</summary>
    public static async Task<IResult> RecibirConceptoAsync(HttpContext http, ISender sender, CancellationToken ct)
    {
        using var cuerpo = new MemoryStream();
        await http.Request.Body.CopyToAsync(cuerpo, ct);

        Result<RegistroClinicoCreadoResponse> result = await sender.Send(new RecibirConceptoMedicoCommand(
            http.Request.Headers[IdempotencyKeyHeader].FirstOrDefault(),
            cuerpo.ToArray()), ct);

        return result.IsSuccess
            ? Results.Created(
                $"/v1/casos/{result.Value.CasoId}/registros-clinicos/{result.Value.RegistroId}",
                result.Value)
            : Problem(result);
    }

    public static async Task<IResult> ConsultarAsync(
        TipoReporte tipo,
        Guid solicitudId,
        ISender sender,
        CancellationToken ct)
    {
        Result<RadicacionResponse> result = await sender.Send(new ConsultarRadicacionQuery(tipo, solicitudId), ct);

        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result);
    }

    /// <summary>
    /// Los errores de validación van como en el contrato: <c>errors</c> por ruta JSON
    /// (<c>{ "evento.fechaHora": ["..."] }</c>). <c>Integracion.ReporteYaRadicado</c> lleva el radicado existente.
    /// </summary>
    internal static IResult Problem(Result result) => result.Error switch
    {
        ValidationError validacion when validacion.Errors.All(e => e is ErrorDeCampo) =>
            CustomResults.Problem(result, new Dictionary<string, object?>
            {
                ["errors"] = validacion.Errors
                    .Cast<ErrorDeCampo>()
                    .GroupBy(e => e.Ruta, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray(), StringComparer.Ordinal)
            }),

        ReporteYaRadicadoError yaRadicado =>
            CustomResults.Problem(result, new Dictionary<string, object?> { ["radicado"] = yaRadicado.Radicado }),

        _ => CustomResults.Problem(result)
    };
}
