using Halsa.Backend.Application.Casos;
using Halsa.Backend.Domain.Casos;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Infrastructure.Database;

internal sealed class CasoMedicoRepository(ApplicationDbContext db) : ICasoMedicoRepository
{
    public void Add(CasoMedico caso) => db.CasosMedicos.Add(caso);

    public Task<CasoMedico?> GetByIdAsync(CasoMedicoId id, CancellationToken cancellationToken = default) =>
        db.CasosMedicos
            .Include(c => c.Remisiones)
            .Include(c => c.Asignaciones)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
}
