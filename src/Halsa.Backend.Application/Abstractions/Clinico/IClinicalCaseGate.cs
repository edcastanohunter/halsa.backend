using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Abstractions.Clinico;

/// <summary>
/// Puerta del lado administrativo: el contexto clínico no ve <c>atel</c>, así que pregunta aquí
/// si el caso existe y si el actor actual lo ve.
/// </summary>
public interface IClinicalCaseGate
{
    /// <summary>Tenant del caso si existe y el actor actual lo ve (por tenant o por remisión vigente). Si no, null.</summary>
    Task<TenantId?> TenantDelCasoVisibleAsync(CasoMedicoId casoId, CancellationToken ct);
}
