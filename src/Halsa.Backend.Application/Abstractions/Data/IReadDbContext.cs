using Halsa.Backend.Application.Integraciones.Registros;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.Domain.Incapacidades;

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

    /// <summary>Las radicaciones del tenant actual (SPEC 05), para la consulta de estado.</summary>
    IQueryable<SolicitudIntegracion> Solicitudes { get; }

    /// <summary>
    /// Las incapacidades visibles (SPEC 07): las del tenant, o las de un caso remitido a la IPS. Solo la parte
    /// administrativa: el diagnóstico vive cifrado en <c>clinico</c>.
    /// </summary>
    IQueryable<Incapacidad> Incapacidades { get; }

    /// <summary>
    /// Los dictámenes PCL visibles (SPEC 08): solo la parte administrativa. La motivación es un registro
    /// <c>DICTAMEN_PCL</c> cifrado en <c>clinico</c>.
    /// </summary>
    IQueryable<DictamenPcl> DictamenesPcl { get; }
}
