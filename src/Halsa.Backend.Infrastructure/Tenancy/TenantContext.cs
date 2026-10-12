using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Infrastructure.Tenancy;

/// <summary>
/// Implementación scoped de <see cref="ITenantContext"/>: lee el <see cref="TenantContextHolder"/> del scope,
/// lo haya llenado el middleware (HTTP) o <see cref="SystemTenantScope"/> (segundo plano).
///
/// Sin contexto, <see cref="Resuelto"/> es <c>false</c> y todas las demás propiedades lanzan
/// <see cref="InvalidOperationException"/>: falla de forma ruidosa y cerrada.
/// </summary>
internal sealed class TenantContext(TenantContextHolder holder) : ITenantContext
{
    public bool Resuelto => holder.Data is not null;

    public ActorTipo Tipo => Require().Tipo;

    public string Sub => Require().Sub;

    public TenantId? TenantId => Require().TenantId;

    public IpsId? IpsId => Require().IpsId;

    private TenantContextData Require() =>
        holder.Data ?? throw new InvalidOperationException(
            "El contexto de tenant no está resuelto. " +
            "Solo se puede acceder a ITenantContext dentro de un request autenticado, " +
            "después de que TenantContextMiddleware lo haya procesado, o dentro de ISystemTenantScope.Entrar.");
}
