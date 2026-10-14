using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Infrastructure.Clinico;

/// <summary>
/// Lee el acceso a un caso con <see cref="ApplicationDbContext"/> (rol <c>halsa_app</c>): una ARL solo ve sus casos
/// y una IPS solo los que tiene remitidos y vigentes. Una consulta por caso, sin caché, porque revocar una remisión o
/// una asignación tiene que cortar el acceso en el siguiente request.
/// </summary>
internal sealed class CasoAccessReader(ApplicationDbContext db) : ICasoAccessReader
{
    public async Task<CasoAcceso?> ObtenerAsync(CasoMedicoId id, CancellationToken ct)
    {
        var caso = await db.CasosMedicos
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new
            {
                c.TenantId,
                Remisiones = c.Remisiones.Where(r => r.RevocadaEn == null).Select(r => r.IpsId).ToList(),
                Asignados = c.Asignaciones.Select(a => new { a.Sub, a.IpsId }).ToList()
            })
            .FirstOrDefaultAsync(ct);

        return caso is null
            ? null
            : new CasoAcceso(
                caso.TenantId,
                caso.Remisiones.ToHashSet(),
                [.. caso.Asignados.Select(a => (a.Sub, a.IpsId))]);
    }
}
