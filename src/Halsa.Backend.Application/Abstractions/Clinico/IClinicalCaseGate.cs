using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Abstractions.Clinico;

/// <summary>
/// Puerta del lado administrativo: el contexto clínico no ve <c>atel</c>, así que pregunta aquí
/// si el caso existe y si el actor actual lo ve.
/// </summary>
public interface IClinicalCaseGate
{
    /// <summary>
    /// El caso si existe y el actor actual lo ve (por tenant o por remisión vigente). Si no, null.
    /// Trae solo lo que el lado clínico necesita para decidir: el tenant, la fecha del evento y el estado.
    /// </summary>
    Task<CasoVisible?> CasoVisibleAsync(CasoMedicoId casoId, CancellationToken ct);
}

/// <summary>Lo que el lado clínico sabe de un caso visible (SPEC 06).</summary>
public sealed record CasoVisible(TenantId TenantId, DateTimeOffset FechaEvento, EstadoCaso Estado);
