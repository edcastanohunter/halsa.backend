using FluentValidation;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.Domain.Webhooks;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Webhooks;

// La API de suscripciones a webhooks salientes (SPEC 10): autoservicio para la service account de integración de una
// ARL o de una IPS, con webhooks:manage. Cada emisor solo ve las suyas (RLS por emisor y el filtro del contexto).

// ── Contratos ─────────────────────────────────────────────────────────────────

/// <summary>El cuerpo de <c>POST /v1/integraciones/suscripciones-webhook</c>.</summary>
public sealed record CrearSuscripcionWebhookRequest(string? Url, IReadOnlyList<string>? Eventos);

/// <summary>El cuerpo de <c>PATCH …/{id}</c>: cambia solo lo que llega.</summary>
/// <param name="Estado"><c>ACTIVA</c> o <c>PAUSADA</c>.</param>
public sealed record CambiarSuscripcionWebhookRequest(string? Url, IReadOnlyList<string>? Eventos, string? Estado);

/// <summary>Una suscripción, sin secreto.</summary>
/// <param name="Estado"><c>ACTIVA</c>, <c>PAUSADA</c> o <c>SUSPENDIDA</c>.</param>
/// <param name="SecretoAnteriorExpiraEn">Hasta cuándo firma también el secreto anterior, tras una rotación.</param>
public sealed record SuscripcionWebhookResponse(
    Guid Id,
    string Url,
    IReadOnlyList<string> Eventos,
    string Estado,
    int FallosConsecutivos,
    DateTimeOffset CreadaEn,
    DateTimeOffset? SecretoAnteriorExpiraEn);

/// <summary>La respuesta <c>201</c>: la suscripción y su <see cref="Secreto"/>, que no vuelve a mostrarse.</summary>
public sealed record SuscripcionWebhookCreadaResponse(
    Guid Id,
    string Url,
    IReadOnlyList<string> Eventos,
    string Estado,
    DateTimeOffset CreadaEn,
    string Secreto);

/// <summary>La respuesta de <c>rotar-secreto</c>: el secreto nuevo, una sola vez.</summary>
public sealed record SecretoRotadoResponse(Guid Id, string Secreto, DateTimeOffset SecretoAnteriorExpiraEn);

/// <summary>Una entrega del historial, sin <c>payload</c>.</summary>
/// <param name="Estado"><c>PENDIENTE</c>, <c>ENTREGADA</c>, <c>DEAD_LETTER</c> o <c>DESCARTADA</c>.</param>
public sealed record EntregaWebhookResponse(
    Guid Id,
    Guid EventoId,
    string Tipo,
    string Estado,
    int Intentos,
    DateTimeOffset PrimerIntentoEn,
    DateTimeOffset ProximoIntentoEn,
    int? UltimoStatus,
    string? UltimoError,
    DateTimeOffset? EntregadaEn);

/// <summary>
/// Las suscripciones y entregas sobre el contexto del request (<c>halsa_app</c>). Lo rastreado lo confirma
/// <see cref="IUnitOfWork.SaveChangesAsync"/>.
/// </summary>
public interface ISuscripcionesWebhookRepository
{
    /// <summary>Las del emisor de la sesión, sin las eliminadas.</summary>
    Task<IReadOnlyList<SuscripcionWebhook>> ListarAsync(CancellationToken ct);

    /// <summary>Rastreada, si es del emisor de la sesión y no está eliminada; si no, null.</summary>
    Task<SuscripcionWebhook?> GetAsync(SuscripcionWebhookId id, CancellationToken ct);

    void Agregar(SuscripcionWebhook suscripcion);

    Task<PagedList<EntregaWebhookResponse>> EntregasAsync(
        SuscripcionWebhookId id, EstadoEntrega? estado, int page, int pageSize, CancellationToken ct);

    /// <summary>Rastreada, si es de <paramref name="suscripcion"/>; si no, null.</summary>
    Task<EntregaWebhook?> EntregaAsync(SuscripcionWebhookId suscripcion, EntregaWebhookId id, CancellationToken ct);

    /// <summary>Pasa a <c>DESCARTADA</c> las pendientes de la suscripción, en una sentencia y en el momento.</summary>
    Task DescartarPendientesAsync(SuscripcionWebhookId id, CancellationToken ct);
}

public static class SuscripcionesWebhookMapeo
{
    public const int PageSizeMaximo = 200;

    public static SuscripcionWebhookResponse Respuesta(SuscripcionWebhook s)
    {
        ArgumentNullException.ThrowIfNull(s);

        return new SuscripcionWebhookResponse(
            s.Id.Value, s.Url, s.Eventos, CodigosDictamen.Codigo(s.Estado), s.FallosConsecutivos, s.CreadaEn,
            s.SecretoAnteriorExpiraEn);
    }

    public static EntregaWebhookResponse Respuesta(EntregaWebhook e)
    {
        ArgumentNullException.ThrowIfNull(e);

        return new EntregaWebhookResponse(
            e.Id.Value, e.EventoId, e.Tipo, CodigosDictamen.Codigo(e.Estado), e.Intentos, e.PrimerIntentoEn,
            e.ProximoIntentoEn, e.UltimoStatus, e.UltimoError, e.EntregadaEn);
    }

    /// <summary>La service account de integración de una ARL o de una IPS: exactamente uno de los dos.</summary>
    internal static bool EsIntegracion(ITenantContext tenantContext) =>
        tenantContext.Resuelto && tenantContext.Tipo == ActorTipo.Integracion &&
        (tenantContext.TenantId is null) != (tenantContext.IpsId is null);
}

// ── Crear ─────────────────────────────────────────────────────────────────────

public sealed record CrearSuscripcionWebhookCommand(CrearSuscripcionWebhookRequest Request)
    : ICommand<SuscripcionWebhookCreadaResponse>;

internal sealed class CrearSuscripcionWebhookCommandHandler(
    ITenantContext tenantContext,
    IWebhookUrlPolicy urlPolicy,
    IWebhookSecretProtector secretos,
    ISuscripcionesWebhookRepository repository,
    IUnitOfWork unitOfWork,
    TimeProvider time)
    : ICommandHandler<CrearSuscripcionWebhookCommand, SuscripcionWebhookCreadaResponse>
{
    public async Task<Result<SuscripcionWebhookCreadaResponse>> Handle(
        CrearSuscripcionWebhookCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!SuscripcionesWebhookMapeo.EsIntegracion(tenantContext))
        {
            return Result.Failure<SuscripcionWebhookCreadaResponse>(WebhookErrors.ActorNoAutorizado);
        }

        if (!urlPolicy.EsValida(command.Request.Url))
        {
            return Result.Failure<SuscripcionWebhookCreadaResponse>(WebhookErrors.UrlInvalida);
        }

        var id = SuscripcionWebhookId.New();
        SecretoNuevo secreto = secretos.Generar(id);

        Result<SuscripcionWebhook> creada = SuscripcionWebhook.Crear(
            id,
            tenantContext.TenantId,
            tenantContext.IpsId,
            command.Request.Url!,
            command.Request.Eventos ?? [],
            secreto.SecretoCt,
            secreto.KeyId,
            tenantContext.Sub,
            time.GetUtcNow());

        if (creada.IsFailure)
        {
            return Result.Failure<SuscripcionWebhookCreadaResponse>(creada.Error);
        }

        repository.Agregar(creada.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        SuscripcionWebhook s = creada.Value;
        return Result.Success(new SuscripcionWebhookCreadaResponse(
            s.Id.Value, s.Url, s.Eventos, CodigosDictamen.Codigo(s.Estado), s.CreadaEn, secreto.Secreto));
    }
}

// ── Consultar ─────────────────────────────────────────────────────────────────

public sealed record ListSuscripcionesWebhookQuery : IQuery<IReadOnlyList<SuscripcionWebhookResponse>>;

internal sealed class ListSuscripcionesWebhookQueryHandler(
    ITenantContext tenantContext,
    ISuscripcionesWebhookRepository repository)
    : IQueryHandler<ListSuscripcionesWebhookQuery, IReadOnlyList<SuscripcionWebhookResponse>>
{
    public async Task<Result<IReadOnlyList<SuscripcionWebhookResponse>>> Handle(
        ListSuscripcionesWebhookQuery query, CancellationToken cancellationToken)
    {
        if (!SuscripcionesWebhookMapeo.EsIntegracion(tenantContext))
        {
            return Result.Failure<IReadOnlyList<SuscripcionWebhookResponse>>(WebhookErrors.ActorNoAutorizado);
        }

        IReadOnlyList<SuscripcionWebhook> suscripciones = await repository.ListarAsync(cancellationToken);

        return Result.Success<IReadOnlyList<SuscripcionWebhookResponse>>(
            [.. suscripciones.Select(SuscripcionesWebhookMapeo.Respuesta)]);
    }
}

public sealed record GetSuscripcionWebhookQuery(Guid Id) : IQuery<SuscripcionWebhookResponse>;

internal sealed class GetSuscripcionWebhookQueryHandler(
    ITenantContext tenantContext,
    ISuscripcionesWebhookRepository repository)
    : IQueryHandler<GetSuscripcionWebhookQuery, SuscripcionWebhookResponse>
{
    public async Task<Result<SuscripcionWebhookResponse>> Handle(
        GetSuscripcionWebhookQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!SuscripcionesWebhookMapeo.EsIntegracion(tenantContext))
        {
            return Result.Failure<SuscripcionWebhookResponse>(WebhookErrors.ActorNoAutorizado);
        }

        SuscripcionWebhook? suscripcion = await repository.GetAsync(new SuscripcionWebhookId(query.Id), cancellationToken);

        return suscripcion is null
            ? Result.Failure<SuscripcionWebhookResponse>(WebhookErrors.SuscripcionNoEncontrada)
            : Result.Success(SuscripcionesWebhookMapeo.Respuesta(suscripcion));
    }
}

// ── Cambiar ───────────────────────────────────────────────────────────────────

public sealed record CambiarSuscripcionWebhookCommand(Guid Id, CambiarSuscripcionWebhookRequest Request)
    : ICommand<SuscripcionWebhookResponse>;

internal sealed class CambiarSuscripcionWebhookCommandHandler(
    ITenantContext tenantContext,
    IWebhookUrlPolicy urlPolicy,
    ISuscripcionesWebhookRepository repository,
    IUnitOfWork unitOfWork)
    : ICommandHandler<CambiarSuscripcionWebhookCommand, SuscripcionWebhookResponse>
{
    public async Task<Result<SuscripcionWebhookResponse>> Handle(
        CambiarSuscripcionWebhookCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!SuscripcionesWebhookMapeo.EsIntegracion(tenantContext))
        {
            return Result.Failure<SuscripcionWebhookResponse>(WebhookErrors.ActorNoAutorizado);
        }

        CambiarSuscripcionWebhookRequest request = command.Request;

        if (request.Url is not null && !urlPolicy.EsValida(request.Url))
        {
            return Result.Failure<SuscripcionWebhookResponse>(WebhookErrors.UrlInvalida);
        }

        EstadoSuscripcion? estado = null;
        if (request.Estado is not null)
        {
            if (!CodigosDictamen.TryParse(request.Estado, out EstadoSuscripcion parseado))
            {
                return Result.Failure<SuscripcionWebhookResponse>(WebhookErrors.EstadoNoPermitido);
            }

            estado = parseado;
        }

        SuscripcionWebhook? suscripcion = await repository.GetAsync(new SuscripcionWebhookId(command.Id), cancellationToken);
        if (suscripcion is null)
        {
            return Result.Failure<SuscripcionWebhookResponse>(WebhookErrors.SuscripcionNoEncontrada);
        }

        Result cambio = suscripcion.Cambiar(request.Url, request.Eventos, estado);
        if (cambio.IsFailure)
        {
            return Result.Failure<SuscripcionWebhookResponse>(cambio.Error);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(SuscripcionesWebhookMapeo.Respuesta(suscripcion));
    }
}

// ── Eliminar ──────────────────────────────────────────────────────────────────

public sealed record EliminarSuscripcionWebhookCommand(Guid Id) : ICommand;

/// <summary>
/// La suscripción pasa a <c>ELIMINADA</c> y después sus entregas pendientes a <c>DESCARTADA</c>. Si el proceso cae entre
/// los dos pasos no se envía nada de más: el dispatcher solo envía las entregas de suscripciones activas.
/// </summary>
internal sealed class EliminarSuscripcionWebhookCommandHandler(
    ITenantContext tenantContext,
    ISuscripcionesWebhookRepository repository,
    IUnitOfWork unitOfWork)
    : ICommandHandler<EliminarSuscripcionWebhookCommand>
{
    public async Task<Result> Handle(EliminarSuscripcionWebhookCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!SuscripcionesWebhookMapeo.EsIntegracion(tenantContext))
        {
            return Result.Failure(WebhookErrors.ActorNoAutorizado);
        }

        var id = new SuscripcionWebhookId(command.Id);
        SuscripcionWebhook? suscripcion = await repository.GetAsync(id, cancellationToken);
        if (suscripcion is null)
        {
            return Result.Failure(WebhookErrors.SuscripcionNoEncontrada);
        }

        Result eliminada = suscripcion.Eliminar();
        if (eliminada.IsFailure)
        {
            return eliminada;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await repository.DescartarPendientesAsync(id, cancellationToken);

        return Result.Success();
    }
}

// ── Rotar el secreto ──────────────────────────────────────────────────────────

public sealed record RotarSecretoWebhookCommand(Guid Id) : ICommand<SecretoRotadoResponse>;

/// <summary>Un secreto nuevo; el actual firma 24 h más. Una segunda rotación descarta el que ya era anterior.</summary>
internal sealed class RotarSecretoWebhookCommandHandler(
    ITenantContext tenantContext,
    IWebhookSecretProtector secretos,
    ISuscripcionesWebhookRepository repository,
    IUnitOfWork unitOfWork,
    TimeProvider time)
    : ICommandHandler<RotarSecretoWebhookCommand, SecretoRotadoResponse>
{
    public async Task<Result<SecretoRotadoResponse>> Handle(
        RotarSecretoWebhookCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!SuscripcionesWebhookMapeo.EsIntegracion(tenantContext))
        {
            return Result.Failure<SecretoRotadoResponse>(WebhookErrors.ActorNoAutorizado);
        }

        SuscripcionWebhook? suscripcion = await repository.GetAsync(new SuscripcionWebhookId(command.Id), cancellationToken);
        if (suscripcion is null)
        {
            return Result.Failure<SecretoRotadoResponse>(WebhookErrors.SuscripcionNoEncontrada);
        }

        // Both under the current KEK: the table keeps one key id for the two.
        byte[] actual = secretos.Reenvolver(suscripcion.Id, suscripcion.SecretoCt, suscripcion.SecretoKeyId);
        SecretoNuevo nuevo = secretos.Generar(suscripcion.Id);

        Result rotado = suscripcion.RotarSecreto(nuevo.SecretoCt, actual, nuevo.KeyId, time.GetUtcNow());
        if (rotado.IsFailure)
        {
            return Result.Failure<SecretoRotadoResponse>(rotado.Error);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new SecretoRotadoResponse(
            suscripcion.Id.Value, nuevo.Secreto, suscripcion.SecretoAnteriorExpiraEn!.Value));
    }
}

// ── Entregas ──────────────────────────────────────────────────────────────────

/// <param name="Estado"><c>PENDIENTE</c>, <c>ENTREGADA</c>, <c>DEAD_LETTER</c>, <c>DESCARTADA</c> o null (todas).</param>
public sealed record ListEntregasWebhookQuery(Guid SuscripcionId, string? Estado, int Page = 1, int PageSize = 50)
    : IQuery<PagedList<EntregaWebhookResponse>>;

internal sealed class ListEntregasWebhookQueryValidator : AbstractValidator<ListEntregasWebhookQuery>
{
    public ListEntregasWebhookQueryValidator()
    {
        RuleFor(q => q.Estado)
            .Must(e => e is null || CodigosDictamen.TryParse<EstadoEntrega>(e, out _))
            .WithErrorCode("Webhook.EstadoEntregaInvalido")
            .WithMessage("Debe ser PENDIENTE, ENTREGADA, DEAD_LETTER o DESCARTADA.");

        RuleFor(q => q.Page).GreaterThanOrEqualTo(1).WithErrorCode("Webhook.PageInvalida");

        RuleFor(q => q.PageSize)
            .InclusiveBetween(1, SuscripcionesWebhookMapeo.PageSizeMaximo).WithErrorCode("Webhook.PageSizeInvalido");
    }
}

internal sealed class ListEntregasWebhookQueryHandler(
    ITenantContext tenantContext,
    ISuscripcionesWebhookRepository repository)
    : IQueryHandler<ListEntregasWebhookQuery, PagedList<EntregaWebhookResponse>>
{
    public async Task<Result<PagedList<EntregaWebhookResponse>>> Handle(
        ListEntregasWebhookQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!SuscripcionesWebhookMapeo.EsIntegracion(tenantContext))
        {
            return Result.Failure<PagedList<EntregaWebhookResponse>>(WebhookErrors.ActorNoAutorizado);
        }

        var id = new SuscripcionWebhookId(query.SuscripcionId);
        if (await repository.GetAsync(id, cancellationToken) is null)
        {
            return Result.Failure<PagedList<EntregaWebhookResponse>>(WebhookErrors.SuscripcionNoEncontrada);
        }

        EstadoEntrega? estado = query.Estado is null ? null : CodigosDictamen.Parse<EstadoEntrega>(query.Estado);

        return await repository.EntregasAsync(id, estado, query.Page, query.PageSize, cancellationToken);
    }
}

// ── Reintentar ────────────────────────────────────────────────────────────────

/// <summary>
/// Vuelve a encolar una entrega en <c>DEAD_LETTER</c> o <c>DESCARTADA</c>: <c>PENDIENTE</c>, con <c>intentos = 0</c> y
/// <c>primer_intento_en = now</c>, así que el siguiente ciclo del dispatcher la envía con una ventana nueva de 24 h.
/// </summary>
public sealed record ReintentarEntregaWebhookCommand(Guid SuscripcionId, Guid EntregaId) : ICommand;

internal sealed class ReintentarEntregaWebhookCommandHandler(
    ITenantContext tenantContext,
    ISuscripcionesWebhookRepository repository,
    IUnitOfWork unitOfWork,
    TimeProvider time)
    : ICommandHandler<ReintentarEntregaWebhookCommand>
{
    public async Task<Result> Handle(ReintentarEntregaWebhookCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!SuscripcionesWebhookMapeo.EsIntegracion(tenantContext))
        {
            return Result.Failure(WebhookErrors.ActorNoAutorizado);
        }

        var suscripcion = new SuscripcionWebhookId(command.SuscripcionId);
        if (await repository.GetAsync(suscripcion, cancellationToken) is null)
        {
            return Result.Failure(WebhookErrors.SuscripcionNoEncontrada);
        }

        EntregaWebhook? entrega = await repository.EntregaAsync(
            suscripcion, new EntregaWebhookId(command.EntregaId), cancellationToken);
        if (entrega is null)
        {
            return Result.Failure(WebhookErrors.EntregaNoEncontrada);
        }

        // Npgsql writes timestamptz only from UTC offsets.
        Result reintento = entrega.Reintentar(time.GetUtcNow().ToUniversalTime());
        if (reintento.IsFailure)
        {
            return reintento;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
