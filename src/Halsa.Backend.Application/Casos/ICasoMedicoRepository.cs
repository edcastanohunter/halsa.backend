using Halsa.Backend.Domain.Casos;

namespace Halsa.Backend.Application.Casos;

/// <summary>
/// Abstracciones de escritura sobre <see cref="CasoMedico"/>.
///
/// El lado de lectura se sirve vía <c>IReadDbContext.CasosMedicos</c> (IQueryable, sin tracking).
/// Este repositorio es para las operaciones que necesitan una instancia rastreada por EF Core.
/// </summary>
public interface ICasoMedicoRepository
{
    /// <summary>Registra un nuevo caso en la unidad de trabajo actual.</summary>
    void Add(CasoMedico caso);

    /// <summary>
    /// Carga una instancia rastreada por id. Incluye remisiones y asignaciones.
    /// Retorna <c>null</c> si el caso no existe o no es visible al tenant actual (RLS).
    /// </summary>
    Task<CasoMedico?> GetByIdAsync(CasoMedicoId id, CancellationToken cancellationToken = default);
}
