using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Clinico.Queries.GetRegistrosClinicos;

internal sealed class GetRegistrosClinicosQueryHandler(
    IClinicalCaseGate gate,
    IClinicalCipher cipher,
    IClinicalDbContext db,
    IAuditScope auditScope)
    : IQueryHandler<GetRegistrosClinicosQuery, IReadOnlyList<RegistroClinicoResponse>>
{
    public async Task<Result<IReadOnlyList<RegistroClinicoResponse>>> Handle(
        GetRegistrosClinicosQuery query,
        CancellationToken cancellationToken)
    {
        TenantId? tenant = await gate.TenantDelCasoVisibleAsync(query.CasoId, cancellationToken);
        if (tenant is null)
        {
            return Result.Failure<IReadOnlyList<RegistroClinicoResponse>>(ClinicoErrors.CasoNoEncontrado);
        }

        auditScope.TenantDelRecurso(tenant.Value);

        List<HistoriaClinicaDetalle> registros = await db.Registros
            .AsNoTracking()
            .Where(r => r.CasoMedicoId == query.CasoId)
            .OrderBy(r => r.RegistradoEn)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

        // If a record was corrected more than once, it points to the latest correction.
        Dictionary<HistoriaClinicaDetalleId, Guid> corregidoPor = registros
            .Where(r => r.CorrigeA is not null)
            .GroupBy(r => r.CorrigeA!.Value)
            .ToDictionary(g => g.Key, g => g.Last().Id.Value);

        var respuesta = new List<RegistroClinicoResponse>(registros.Count);

        foreach (HistoriaClinicaDetalle registro in registros)
        {
            byte[] claro = await cipher.DescifrarAsync(
                registro.TenantId, registro.Id.Value, RegistroClinicoEscritura.CampoContenido,
                registro.Contenido, cancellationToken);

            respuesta.Add(new RegistroClinicoResponse(
                registro.Id.Value,
                registro.CasoMedicoId.Value,
                registro.Tipo,
                registro.Profesional,
                registro.AtendidoEn,
                registro.RegistradoEn,
                registro.RetenerHasta,
                registro.CorrigeA?.Value,
                corregidoPor.TryGetValue(registro.Id, out Guid nuevo) ? nuevo : null,
                RegistroClinicoEscritura.Deserializar<ContenidoClinico>(claro)));
        }

        return Result.Success<IReadOnlyList<RegistroClinicoResponse>>(respuesta);
    }
}
