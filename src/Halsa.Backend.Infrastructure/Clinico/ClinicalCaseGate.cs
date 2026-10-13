using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Infrastructure.Clinico;

/// <summary>
/// Pregunta al lado administrativo si el actor ve el caso. Va por <see cref="ApplicationDbContext"/>
/// (rol <c>halsa_app</c>), así que la respuesta pasa por el query filter y la RLS de la SPEC 02:
/// una ARL solo ve sus casos y una IPS solo los que tiene remitidos y vigentes.
/// </summary>
internal sealed class ClinicalCaseGate(ApplicationDbContext db) : IClinicalCaseGate
{
    public Task<CasoVisible?> CasoVisibleAsync(CasoMedicoId casoId, CancellationToken ct) =>
        db.CasosMedicos
            .AsNoTracking()
            .Where(c => c.Id == casoId)
            .Select(c => new CasoVisible(c.TenantId, c.FechaEvento, c.Estado))
            .FirstOrDefaultAsync(ct);
}
