using FluentValidation;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Plazos;

/// <summary>Los códigos de estado de un plazo, como los ve el contrato.</summary>
public static class CodigosPlazo
{
    public static readonly IReadOnlyList<string> Estados = [.. Enum.GetValues<EstadoPlazo>().Select(e => CodigosDictamen.Codigo(e))];

    public static string Codigo(EstadoPlazo estado) => CodigosDictamen.Codigo(estado);
}

internal static class PlazosLectura
{
    /// <summary>Solo el personal de una ARL: los plazos son del tenant, y una IPS no los ve.</summary>
    public static bool PuedeLeer(ITenantContext tenantContext) =>
        tenantContext.Resuelto && tenantContext.Tipo == ActorTipo.Arl && tenantContext.TenantId is not null;

    /// <summary>
    /// Los plazos con la base legal de su regla, ordenados por vencimiento, y la página pedida. Se materializan y se
    /// mapean en memoria: los ids son tipos propios con conversión.
    /// </summary>
    public static async Task<List<PlazoResponse>> ListarAsync(
        IReadDbContext db, IQueryable<Plazo> plazos, int? saltar, int? tomar, CancellationToken ct)
    {
        var consulta = plazos
            .Join(db.ReglasTermino, p => p.ReglaId, r => r.Id, (p, r) => new { Plazo = p, r.BaseLegal })
            .OrderBy(x => x.Plazo.VenceEn)
            .ThenBy(x => x.Plazo.ReglaCodigo)
            .AsQueryable();

        if (saltar is { } s)
        {
            consulta = consulta.Skip(s);
        }

        if (tomar is { } t)
        {
            consulta = consulta.Take(t);
        }

        var filas = await consulta.ToListAsync(ct);
        return [.. filas.Select(x => Respuesta(x.Plazo, x.BaseLegal))];
    }

    private static PlazoResponse Respuesta(Plazo plazo, string baseLegal) => new(
        plazo.Id.Value,
        plazo.CasoMedicoId.Value,
        plazo.Radicado,
        plazo.ReglaCodigo,
        baseLegal,
        plazo.ReferenciaId,
        plazo.IniciaEn,
        plazo.VenceEn,
        plazo.AlertaEn,
        CodigosPlazo.Codigo(plazo.Estado),
        plazo.Extemporaneo,
        plazo.CumplidoEn);
}

// ── Los plazos de un caso ─────────────────────────────────────────────────────

/// <summary>
/// <c>GET /v1/casos/{id}/plazos</c> (SPEC 09): todos los plazos del caso, de cualquier estado, con su base legal. Se
/// audita como <c>LEER / PLAZO</c>.
/// </summary>
public sealed record ListPlazosCasoQuery(CasoMedicoId CasoId) : IQuery<IReadOnlyList<PlazoResponse>>, IAuditedQuery
{
    public AccionAuditoria Accion => AccionAuditoria.Leer;

    public ObjetivoAuditoria Objetivo => new(RecursoAuditoria.Plazo, null, CasoId.Value);
}

internal sealed class ListPlazosCasoQueryHandler(IReadDbContext db, ITenantContext tenantContext, IAuditScope auditScope)
    : IQueryHandler<ListPlazosCasoQuery, IReadOnlyList<PlazoResponse>>
{
    public async Task<Result<IReadOnlyList<PlazoResponse>>> Handle(ListPlazosCasoQuery query, CancellationToken cancellationToken)
    {
        if (!PlazosLectura.PuedeLeer(tenantContext))
        {
            return Result.Failure<IReadOnlyList<PlazoResponse>>(PlazoErrors.ActorNoAutorizado);
        }

        bool visible = await db.CasosMedicos.AsNoTracking().AnyAsync(c => c.Id == query.CasoId, cancellationToken);

        // Invisible and absent answer the same: 404, never 403.
        if (!visible)
        {
            return Result.Failure<IReadOnlyList<PlazoResponse>>(CasoMedicoErrors.NoEncontrado);
        }

        auditScope.TenantDelRecurso(tenantContext.TenantId!.Value);

        List<PlazoResponse> plazos = await PlazosLectura.ListarAsync(
            db, db.Plazos.Where(p => p.CasoMedicoId == query.CasoId), saltar: null, tomar: null, cancellationToken);

        return plazos;
    }
}

// ── Los plazos del tenant ─────────────────────────────────────────────────────

/// <summary>
/// <c>GET /v1/plazos</c> (SPEC 09): los plazos del tenant, paginados, ordenados por vencimiento. Filtra por estado, por
/// regla y por vencimiento anterior a una fecha. Se audita como <c>LEER / PLAZO</c>.
/// </summary>
/// <param name="Estado"><c>ABIERTO</c>, <c>CUMPLIDO</c>, <c>VENCIDO</c> o <c>SUSPENDIDO</c>.</param>
/// <param name="VenceAntesDe">Exclusivo.</param>
public sealed record ListPlazosQuery(
    string? Estado,
    DateTimeOffset? VenceAntesDe,
    string? Regla,
    int Page = 1,
    int PageSize = 50)
    : IQuery<PagedList<PlazoResponse>>, IAuditedQuery
{
    public const int PageSizeMaximo = 200;

    public AccionAuditoria Accion => AccionAuditoria.Leer;

    public ObjetivoAuditoria Objetivo => new(RecursoAuditoria.Plazo, null, null);
}

internal sealed class ListPlazosQueryValidator : AbstractValidator<ListPlazosQuery>
{
    public ListPlazosQueryValidator()
    {
        RuleFor(q => q.Estado)
            .Must(e => e is null || CodigosPlazo.Estados.Contains(e)).WithErrorCode("Plazo.EstadoInvalido")
            .WithMessage("Debe ser ABIERTO, CUMPLIDO, VENCIDO o SUSPENDIDO.");

        RuleFor(q => q.Regla).MaximumLength(50).WithErrorCode("Plazo.ReglaInvalida");

        RuleFor(q => q.Page).GreaterThanOrEqualTo(1).WithErrorCode("Plazo.PageInvalida");

        RuleFor(q => q.PageSize)
            .InclusiveBetween(1, ListPlazosQuery.PageSizeMaximo).WithErrorCode("Plazo.PageSizeInvalido");
    }
}

internal sealed class ListPlazosQueryHandler(IReadDbContext db, ITenantContext tenantContext, IAuditScope auditScope)
    : IQueryHandler<ListPlazosQuery, PagedList<PlazoResponse>>
{
    public async Task<Result<PagedList<PlazoResponse>>> Handle(ListPlazosQuery query, CancellationToken cancellationToken)
    {
        if (!PlazosLectura.PuedeLeer(tenantContext))
        {
            return Result.Failure<PagedList<PlazoResponse>>(PlazoErrors.ActorNoAutorizado);
        }

        auditScope.TenantDelRecurso(tenantContext.TenantId!.Value);

        IQueryable<Plazo> plazos = db.Plazos;

        if (query.Estado is not null)
        {
            EstadoPlazo estado = CodigosDictamen.Parse<EstadoPlazo>(query.Estado);
            plazos = plazos.Where(p => p.Estado == estado);
        }

        if (query.Regla is not null)
        {
            plazos = plazos.Where(p => p.ReglaCodigo == query.Regla);
        }

        if (query.VenceAntesDe is { } venceAntesDe)
        {
            DateTimeOffset limite = venceAntesDe.ToUniversalTime();
            plazos = plazos.Where(p => p.VenceEn < limite);
        }

        int total = await plazos.CountAsync(cancellationToken);
        List<PlazoResponse> pagina = await PlazosLectura.ListarAsync(
            db, plazos, (query.Page - 1) * query.PageSize, query.PageSize, cancellationToken);

        return new PagedList<PlazoResponse>(pagina, query.Page, query.PageSize, total);
    }
}
