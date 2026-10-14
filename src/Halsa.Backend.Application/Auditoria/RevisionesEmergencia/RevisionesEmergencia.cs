using FluentValidation;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Auditoria.RevisionesEmergencia;

/// <summary>Estados y conclusiones de una revisión de emergencia, como los ve el contrato.</summary>
public static class CodigosRevision
{
    public const string Pendiente = "PENDIENTE";
    public const string Cerrada = "CERRADA";
    public const string Justificado = "JUSTIFICADO";
    public const string Injustificado = "INJUSTIFICADO";

    public static readonly IReadOnlyList<string> Estados = [Pendiente, Cerrada];

    public static readonly IReadOnlyList<string> Conclusiones = [Justificado, Injustificado];

    public const int MaximoObservaciones = 2000;
}

/// <summary>Una revisión de un acceso de emergencia (SPEC 08).</summary>
/// <param name="Estado"><c>PENDIENTE</c> o <c>CERRADA</c>.</param>
/// <param name="AccesoId">La fila de <c>auditoria.accesos</c> que la abrió.</param>
public sealed record RevisionEmergenciaResponse(
    Guid Id,
    Guid CasoMedicoId,
    Guid AccesoId,
    string ActorId,
    string Motivo,
    DateTimeOffset OcurridoEn,
    DateTimeOffset VenceRevisionEn,
    string Estado,
    DateTimeOffset? RevisadoEn,
    string? RevisadoPor,
    string? Conclusion,
    string? Observaciones);

public enum ResultadoCierreRevision
{
    Cerrada,
    NoEncontrada,
    YaCerrada
}

/// <summary>
/// <c>auditoria.revisiones_emergencia</c> sobre el contexto del request (<c>halsa_app</c>): la RLS limita la lectura
/// y el cierre al tenant de la sesión, además del filtro explícito.
/// </summary>
public interface IRevisionesEmergenciaStore
{
    /// <param name="pendientes"><c>true</c>: sin cerrar; <c>false</c>: cerradas; <c>null</c>: todas.</param>
    Task<PagedList<RevisionEmergenciaResponse>> ListarAsync(
        TenantId tenant, bool? pendientes, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Marca el cierre en la unidad de trabajo del request; lo confirma su <c>SaveChangesAsync</c>. El caso de la
    /// revisión sale en <paramref name="casoMedicoId"/>, para la auditoría.
    /// </summary>
    Task<(ResultadoCierreRevision Resultado, Guid? CasoMedicoId)> CerrarAsync(
        TenantId tenant, Guid id, string revisadoPor, string conclusion, string? observaciones, DateTimeOffset ahora,
        CancellationToken ct);
}

public static class RevisionEmergenciaErrors
{
    public static readonly Error NoEncontrada =
        Error.NotFound("Auditoria.RevisionNoEncontrada",
            "La revisión de emergencia no existe o no es de este tenant.");

    public static readonly Error YaCerrada =
        Error.Conflict("Auditoria.RevisionYaCerrada",
            "La revisión de emergencia ya está cerrada y no cambia.");
}

// ── Listar ────────────────────────────────────────────────────────────────────

/// <summary>
/// Las revisiones de emergencia del tenant del oficial de privacidad (SPEC 08), de la más reciente a la más antigua.
/// La consulta se audita a sí misma (<c>LEER / AUDITORIA</c>).
/// </summary>
/// <param name="Estado"><c>PENDIENTE</c>, <c>CERRADA</c> o null (todas).</param>
public sealed record ListRevisionesEmergenciaQuery(string? Estado, int Page = 1, int PageSize = 50)
    : IQuery<PagedList<RevisionEmergenciaResponse>>, IAuditedQuery
{
    public const int PageSizeMaximo = 200;

    public AccionAuditoria Accion => AccionAuditoria.Leer;

    public ObjetivoAuditoria Objetivo => new(RecursoAuditoria.Auditoria, null, null);
}

internal sealed class ListRevisionesEmergenciaQueryValidator : AbstractValidator<ListRevisionesEmergenciaQuery>
{
    public ListRevisionesEmergenciaQueryValidator()
    {
        RuleFor(q => q.Estado)
            .Must(e => e is null || CodigosRevision.Estados.Contains(e)).WithErrorCode("Auditoria.EstadoInvalido")
            .WithMessage("Debe ser PENDIENTE o CERRADA.");

        RuleFor(q => q.Page).GreaterThanOrEqualTo(1).WithErrorCode("Auditoria.PageInvalida");

        RuleFor(q => q.PageSize)
            .InclusiveBetween(1, ListRevisionesEmergenciaQuery.PageSizeMaximo).WithErrorCode("Auditoria.PageSizeInvalido");
    }
}

internal sealed class ListRevisionesEmergenciaQueryHandler(
    IRevisionesEmergenciaStore store,
    ITenantContext tenantContext,
    IAuditScope auditScope)
    : IQueryHandler<ListRevisionesEmergenciaQuery, PagedList<RevisionEmergenciaResponse>>
{
    public async Task<Result<PagedList<RevisionEmergenciaResponse>>> Handle(
        ListRevisionesEmergenciaQuery query, CancellationToken cancellationToken)
    {
        // Only an ARL oversees the emergency accesses to its cases, even if another actor holds auditoria:revisar.
        if (!tenantContext.Resuelto || tenantContext.Tipo != ActorTipo.Arl || tenantContext.TenantId is not { } tenant)
        {
            return Result.Failure<PagedList<RevisionEmergenciaResponse>>(AuditoriaErrors.ActorNoAutorizado);
        }

        auditScope.TenantDelRecurso(tenant);

        bool? pendientes = query.Estado switch
        {
            CodigosRevision.Pendiente => true,
            CodigosRevision.Cerrada => false,
            _ => null
        };

        return await store.ListarAsync(tenant, pendientes, query.Page, query.PageSize, cancellationToken);
    }
}

// ── Cerrar ────────────────────────────────────────────────────────────────────

/// <summary>
/// Cierra una revisión de emergencia con su conclusión (SPEC 08). Una vez cerrada no cambia: un segundo cierre
/// responde <see cref="RevisionEmergenciaErrors.YaCerrada"/>.
/// </summary>
public sealed record CerrarRevisionEmergenciaCommand(Guid Id, string? Conclusion, string? Observaciones) : ICommand;

internal sealed class CerrarRevisionEmergenciaCommandValidator : AbstractValidator<CerrarRevisionEmergenciaCommand>
{
    public CerrarRevisionEmergenciaCommandValidator()
    {
        RuleFor(c => c.Conclusion)
            .Must(c => c is not null && CodigosRevision.Conclusiones.Contains(c))
            .WithErrorCode("Auditoria.ConclusionInvalida")
            .WithMessage("Debe ser JUSTIFICADO o INJUSTIFICADO.");

        RuleFor(c => c.Observaciones)
            .MaximumLength(CodigosRevision.MaximoObservaciones).WithErrorCode("Auditoria.ObservacionesDemasiadoLargas")
            .WithMessage("Admite como máximo {MaxLength} caracteres.");
    }
}

internal sealed class CerrarRevisionEmergenciaCommandHandler(
    IRevisionesEmergenciaStore store,
    ITenantContext tenantContext,
    IAuditTrail auditTrail,
    IUnitOfWork unitOfWork,
    TimeProvider time)
    : ICommandHandler<CerrarRevisionEmergenciaCommand>
{
    public async Task<Result> Handle(CerrarRevisionEmergenciaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!tenantContext.Resuelto || tenantContext.Tipo != ActorTipo.Arl || tenantContext.TenantId is not { } tenant)
        {
            return Result.Failure(AuditoriaErrors.ActorNoAutorizado);
        }

        (ResultadoCierreRevision resultado, Guid? casoId) = await store.CerrarAsync(
            tenant, command.Id, tenantContext.Sub, command.Conclusion!, command.Observaciones, time.GetUtcNow(),
            cancellationToken);

        switch (resultado)
        {
            case ResultadoCierreRevision.NoEncontrada:
                return Result.Failure(RevisionEmergenciaErrors.NoEncontrada);
            case ResultadoCierreRevision.YaCerrada:
                return Result.Failure(RevisionEmergenciaErrors.YaCerrada);
        }

        // The closure and its audit row commit together.
        auditTrail.Registrar(
            AccionAuditoria.Actualizar,
            new ObjetivoAuditoria(RecursoAuditoria.Auditoria, command.Id.ToString(), casoId),
            tenant);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            // Another officer closed it between the read and this save: revisado_en is the concurrency token.
            return Result.Failure(RevisionEmergenciaErrors.YaCerrada);
        }

        return Result.Success();
    }
}
