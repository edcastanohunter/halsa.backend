using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Application.Abstractions.Clinico;

/// <summary>
/// El único acceso al esquema <c>clinico</c>. Lo implementa un <c>DbContext</c> propio, conectado
/// como <c>halsa_clinico</c>. Solo lo usa <c>Halsa.Backend.Application.Clinico</c> (test de arquitectura).
///
/// Append-only: se puede leer y dar de alta, pero no hay forma de modificar ni de borrar. Del outbox, solo inserta.
/// </summary>
public interface IClinicalDbContext
{
    /// <summary>Registros visibles para el actor actual (query filter y RLS clínica).</summary>
    IQueryable<HistoriaClinicaDetalle> Registros { get; }

    /// <summary>La metadata de los archivos soporte visibles (SPEC 07); solo lectura (SPEC 08).</summary>
    IQueryable<Soporte> Soportes { get; }

    void Add(HistoriaClinicaDetalle registro);

    /// <summary>Encola un evento de integración en el outbox, en la transacción del registro (SPEC 10).</summary>
    void Encolar(IIntegrationEvent evento);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
