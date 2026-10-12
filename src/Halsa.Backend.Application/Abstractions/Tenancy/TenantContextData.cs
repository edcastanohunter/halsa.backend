using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Abstractions.Tenancy;

/// <summary>
/// Contexto de tenant inmutable. Lo produce <c>TenantContextResolver</c> al procesar el token (y el middleware
/// lo guarda en el <c>TenantContextHolder</c> del request) o <see cref="ISystemTenantScope"/> en segundo plano.
/// </summary>
public sealed record TenantContextData(
    ActorTipo Tipo,
    string Sub,
    TenantId? TenantId,
    IpsId? IpsId);
