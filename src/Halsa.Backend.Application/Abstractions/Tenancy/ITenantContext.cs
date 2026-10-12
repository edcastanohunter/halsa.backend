using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Abstractions.Tenancy;

/// <summary>
/// Contexto de tenant resuelto a partir del token validado del request actual.
/// Disponible en cualquier componente scoped durante un request HTTP autenticado.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// <c>true</c> cuando el contexto fue resuelto desde un token válido.
    /// <c>false</c> fuera de un request autenticado o antes de que el middleware lo resuelva.
    /// Las demás propiedades lanzan <see cref="InvalidOperationException"/> si es <c>false</c>.
    /// </summary>
    bool Resuelto { get; }

    ActorTipo Tipo { get; }

    /// <summary>Claim <c>sub</c> del token, tal cual llega de Keycloak.</summary>
    string Sub { get; }

    /// <summary>
    /// Código ARL (tenant). <c>null</c> para <see cref="ActorTipo.Ips"/> y <see cref="ActorTipo.Paciente"/>.
    /// </summary>
    TenantId? TenantId { get; }

    /// <summary>
    /// Código REPS de la IPS. Solo presente para <see cref="ActorTipo.Ips"/> e
    /// <see cref="ActorTipo.Integracion"/> de IPS.
    /// </summary>
    IpsId? IpsId { get; }
}
