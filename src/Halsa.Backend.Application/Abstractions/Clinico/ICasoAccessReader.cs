using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Abstractions.Clinico;

/// <summary>
/// Lo que el ABAC clínico (SPEC 08) necesita saber de un caso: de quién es, a qué IPS está remitido hoy y quién
/// está asignado. Lee con <c>halsa_app</c>, así que pasa por el query filter y la RLS de la SPEC 02.
/// </summary>
public interface ICasoAccessReader
{
    /// <summary>El acceso al caso, o <c>null</c> si no existe o el actor actual no lo ve.</summary>
    Task<CasoAcceso?> ObtenerAsync(CasoMedicoId id, CancellationToken ct);
}

/// <summary>
/// El tenant del caso, las IPS con una remisión vigente y el equipo asignado. <c>IpsId</c> de un asignado es la IPS
/// que lo asignó, o <c>null</c> si lo asignó la ARL.
/// </summary>
public sealed record CasoAcceso(
    TenantId TenantId,
    IReadOnlySet<IpsId> RemisionesVigentes,
    IReadOnlyList<(string Sub, IpsId? IpsId)> Asignados);
