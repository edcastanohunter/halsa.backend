using Halsa.Backend.Domain.Casos;

namespace Halsa.Backend.Application.Abstractions.Data;

/// <summary>
/// The read side. Exposes <see cref="IQueryable{T}"/> only: queries compose, but nothing on the
/// read path can mutate. Projections write through the concrete context in the infrastructure layer.
/// </summary>
public interface IReadDbContext
{
    /// <summary>
    /// All <see cref="CasoMedico"/> rows visible to the current tenant context.
    /// An EF Core global query filter enforces tenant/IPS isolation; PostgreSQL RLS provides
    /// a second layer of enforcement independently.
    /// </summary>
    IQueryable<CasoMedico> CasosMedicos { get; }
}
