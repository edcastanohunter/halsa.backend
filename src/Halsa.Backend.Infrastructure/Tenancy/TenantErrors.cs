using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Infrastructure.Tenancy;

/// <summary>
/// Errores que puede producir la resolución del contexto de tenant.
/// Ambos se traducen a HTTP 403 con ProblemDetails por <c>TenantContextMiddleware</c>.
/// </summary>
internal static class TenantErrors
{
    /// <summary>
    /// El claim <c>actor_tipo</c> está ausente, tiene un valor reservado (<c>paciente</c>,
    /// <c>sistema</c>) o un valor desconocido en el realm <c>halsa</c>.
    /// </summary>
    public static readonly Error ActorTipoInvalido = Error.Forbidden(
        "Tenant.ActorTipoInvalido",
        "El token no incluye un actor_tipo válido para este realm.");

    /// <summary>
    /// El <c>actor_tipo</c> es reconocido pero los claims obligatorios para ese tipo
    /// (p. ej. <c>tenant_id</c> para <c>arl</c>) están ausentes o tienen un formato inválido.
    /// </summary>
    public static readonly Error ClaimsIncompletos = Error.Forbidden(
        "Tenant.ClaimsIncompletos",
        "El token no incluye todos los claims necesarios para derivar el contexto de tenant.");
}
