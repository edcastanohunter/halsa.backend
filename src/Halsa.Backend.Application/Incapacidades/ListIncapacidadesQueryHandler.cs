using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Incapacidades;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Incapacidades;

internal sealed class ListIncapacidadesQueryHandler(
    IReadDbContext db,
    ITenantContext tenantContext,
    IAuditScope auditScope,
    IncapacidadesOptions opciones)
    : IQueryHandler<ListIncapacidadesQuery, IReadOnlyList<IncapacidadResponse>>
{
    public async Task<Result<IReadOnlyList<IncapacidadResponse>>> Handle(
        ListIncapacidadesQuery query,
        CancellationToken cancellationToken)
    {
        // A person of an ARL, or of an IPS through a live remission. The integrations have no read channel here.
        if (tenantContext.Tipo is not (ActorTipo.Arl or ActorTipo.Ips))
        {
            return Result.Failure<IReadOnlyList<IncapacidadResponse>>(IncapacidadConsultaErrors.ActorNoAutorizado);
        }

        var caso = await db.CasosMedicos
            .AsNoTracking()
            .Where(c => c.Id == query.CasoId)
            .Select(c => new { c.TenantId })
            .FirstOrDefaultAsync(cancellationToken);

        // Invisible and absent answer the same: 404, never 403.
        if (caso is null)
        {
            return Result.Failure<IReadOnlyList<IncapacidadResponse>>(IncapacidadCargaErrors.CasoNoEncontrado);
        }

        auditScope.TenantDelRecurso(caso.TenantId);

        List<Incapacidad> incapacidades = await db.Incapacidades
            .Where(i => i.CasoMedicoId == query.CasoId)
            .OrderBy(i => i.FechaInicio)
            .ToListAsync(cancellationToken);

        // The alerts are computed, never stored.
        ParametrosIncapacidad parametros = opciones.Parametros();
        return Result.Success<IReadOnlyList<IncapacidadResponse>>(
            [.. incapacidades.Select(i => IncapacidadResponse.Desde(i, parametros))]);
    }
}
