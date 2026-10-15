using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.Webhooks;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Microsoft.AspNetCore.Mvc;

namespace Halsa.Backend.Api.Endpoints.Integraciones.Webhooks;

// La API de suscripciones a webhooks salientes (SPEC 10). Todas exigen webhooks:manage y un actor de integración (de
// ARL o de IPS): con otro actor, 403. Una suscripción de otro emisor es 404, igual que una inexistente.

internal static class SuscripcionesWebhookRutas
{
    public const string Base = "integraciones/suscripciones-webhook";
}

/// <summary>
/// <c>POST /v1/integraciones/suscripciones-webhook</c> — <c>{ url, eventos[] }</c>. <c>201</c> con el <c>secreto</c>, que
/// no se vuelve a mostrar; <c>400</c> si la URL no es <c>https://</c> pública, sin credenciales y de 2048 caracteres como
/// máximo, o si un evento no es del destinatario del emisor (una ARL no se suscribe a <c>caso.remitido</c>).
/// </summary>
internal sealed class CrearSuscripcionWebhook : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost(SuscripcionesWebhookRutas.Base, async (
                CrearSuscripcionWebhookRequest request,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(new CrearSuscripcionWebhookCommand(request), ct);

                return result.IsSuccess
                    ? Results.Created($"/v1/{SuscripcionesWebhookRutas.Base}/{result.Value.Id}", result.Value)
                    : CustomResults.Problem(result);
            })
        .WithTags(Tags.Integraciones)
        .WithName("CrearSuscripcionWebhook")
        .WithSummary("Registra una suscripción a webhooks salientes. El secreto se devuelve una sola vez.")
        .Produces<SuscripcionWebhookCreadaResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.WebhooksManage);
}

/// <summary><c>GET /v1/integraciones/suscripciones-webhook</c> — Las suscripciones del emisor, sin secretos.</summary>
internal sealed class ListSuscripcionesWebhook : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet(SuscripcionesWebhookRutas.Base, async (ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ListSuscripcionesWebhookQuery(), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Integraciones)
        .WithName("ListSuscripcionesWebhook")
        .WithSummary("Las suscripciones a webhooks del emisor, sin secretos.")
        .Produces<IReadOnlyList<SuscripcionWebhookResponse>>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.WebhooksManage);
}

/// <summary><c>GET /v1/integraciones/suscripciones-webhook/{id}</c> — El detalle, sin secreto.</summary>
internal sealed class GetSuscripcionWebhook : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet($"{SuscripcionesWebhookRutas.Base}/{{id:guid}}", async (Guid id, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new GetSuscripcionWebhookQuery(id), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Integraciones)
        .WithName("GetSuscripcionWebhook")
        .WithSummary("El detalle de una suscripción a webhooks, sin secreto.")
        .Produces<SuscripcionWebhookResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequirePermission(Permissions.WebhooksManage);
}

/// <summary>
/// <c>PATCH /v1/integraciones/suscripciones-webhook/{id}</c> — <c>{ url?, eventos?, estado? }</c>, con
/// <c>estado</c> <c>ACTIVA</c> o <c>PAUSADA</c>. <c>200</c>. Reactivar una <c>SUSPENDIDA</c> pone
/// <c>fallosConsecutivos = 0</c>.
/// </summary>
internal sealed class CambiarSuscripcionWebhook : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPatch($"{SuscripcionesWebhookRutas.Base}/{{id:guid}}", async (
                Guid id,
                CambiarSuscripcionWebhookRequest request,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(new CambiarSuscripcionWebhookCommand(id, request), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Integraciones)
        .WithName("CambiarSuscripcionWebhook")
        .WithSummary("Cambia la URL, los eventos o el estado (ACTIVA o PAUSADA) de una suscripción.")
        .Produces<SuscripcionWebhookResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequirePermission(Permissions.WebhooksManage);
}

/// <summary>
/// <c>DELETE /v1/integraciones/suscripciones-webhook/{id}</c> — <c>204</c>. Pasa a <c>ELIMINADA</c> y sus entregas
/// pendientes a <c>DESCARTADA</c>.
/// </summary>
internal sealed class EliminarSuscripcionWebhook : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapDelete($"{SuscripcionesWebhookRutas.Base}/{{id:guid}}", async (Guid id, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new EliminarSuscripcionWebhookCommand(id), ct);

                return result.IsSuccess ? Results.NoContent() : CustomResults.Problem(result);
            })
        .WithTags(Tags.Integraciones)
        .WithName("EliminarSuscripcionWebhook")
        .WithSummary("Da de baja una suscripción y descarta sus entregas pendientes.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequirePermission(Permissions.WebhooksManage);
}

/// <summary>
/// <c>POST /v1/integraciones/suscripciones-webhook/{id}/rotar-secreto</c> — <c>200</c> con el <c>secreto</c> nuevo. El
/// anterior sigue firmando 24 h más.
/// </summary>
internal sealed class RotarSecretoWebhook : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost($"{SuscripcionesWebhookRutas.Base}/{{id:guid}}/rotar-secreto", async (
                Guid id,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(new RotarSecretoWebhookCommand(id), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Integraciones)
        .WithName("RotarSecretoWebhook")
        .WithSummary("Genera un secreto nuevo. El anterior firma 24 h más.")
        .Produces<SecretoRotadoResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequirePermission(Permissions.WebhooksManage);
}

/// <summary>
/// <c>GET /v1/integraciones/suscripciones-webhook/{id}/entregas?estado=&amp;page=&amp;pageSize=</c> — El historial de
/// entregas, de la más reciente a la más antigua, sin <c>payload</c>.
/// </summary>
internal sealed class ListEntregasWebhook : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet($"{SuscripcionesWebhookRutas.Base}/{{id:guid}}/entregas", async (
                Guid id,
                [AsParameters] Request request,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(
                    new ListEntregasWebhookQuery(id, request.Estado, request.Page ?? 1, request.PageSize ?? 50), ct);

                return result.IsSuccess ? Results.Ok(result.Value) : CustomResults.Problem(result);
            })
        .WithTags(Tags.Integraciones)
        .WithName("ListEntregasWebhook")
        .WithSummary("El historial de entregas de una suscripción, sin payload.")
        .Produces<PagedList<EntregaWebhookResponse>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequirePermission(Permissions.WebhooksManage);

    internal sealed record Request(
        [property: FromQuery(Name = "estado")] string? Estado,
        [property: FromQuery(Name = "page")] int? Page,
        [property: FromQuery(Name = "pageSize")] int? PageSize);
}

/// <summary>
/// <c>POST /v1/integraciones/suscripciones-webhook/{id}/entregas/{entregaId}/reintentar</c> — Solo desde
/// <c>DEAD_LETTER</c> o <c>DESCARTADA</c>: vuelve a <c>PENDIENTE</c> con <c>intentos = 0</c> y una ventana nueva de 24 h, y
/// el siguiente ciclo del dispatcher la envía. <c>202</c>; <c>409</c> desde cualquier otro estado.
/// </summary>
internal sealed class ReintentarEntregaWebhook : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost($"{SuscripcionesWebhookRutas.Base}/{{id:guid}}/entregas/{{entregaId:guid}}/reintentar", async (
                Guid id,
                Guid entregaId,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(new ReintentarEntregaWebhookCommand(id, entregaId), ct);

                return result.IsSuccess ? Results.Accepted() : CustomResults.Problem(result);
            })
        .WithTags(Tags.Integraciones)
        .WithName("ReintentarEntregaWebhook")
        .WithSummary("Vuelve a encolar una entrega en DEAD_LETTER o DESCARTADA.")
        .Produces(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequirePermission(Permissions.WebhooksManage);
}
