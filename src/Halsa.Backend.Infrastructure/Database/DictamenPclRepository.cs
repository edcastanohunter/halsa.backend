using Halsa.Backend.Application.Dictamenes;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Dictamenes;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Infrastructure.Database;

/// <summary>
/// El dictamen rastreado por <see cref="ApplicationDbContext"/> (<c>halsa_app</c>). Guardarlo actualiza solo lo que
/// cambió <c>Notificar</c>: las tres columnas de la notificación, las únicas que el rol puede actualizar.
/// </summary>
internal sealed class DictamenPclRepository(ApplicationDbContext db) : IDictamenPclRepository
{
    public Task<DictamenPcl?> VigenteAsync(CasoMedicoId casoId, CancellationToken ct) =>
        db.DictamenesPcl.FirstOrDefaultAsync(d => d.CasoMedicoId == casoId && d.CorregidoPor == null, ct);
}
