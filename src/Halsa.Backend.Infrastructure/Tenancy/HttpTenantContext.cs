using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Halsa.Backend.Infrastructure.Tenancy;

/// <summary>
/// Implementación scoped de <see cref="ITenantContext"/> que lee el <see cref="TenantContextData"/>
/// que el <c>TenantContextMiddleware</c> depositó en <c>HttpContext.Items</c>.
///
/// Fuera de un request autenticado, <see cref="Resuelto"/> es <c>false</c> y todas las
/// demás propiedades lanzan <see cref="InvalidOperationException"/>.
/// </summary>
internal sealed class HttpTenantContext(IHttpContextAccessor httpContextAccessor) : ITenantContext
{
    private TenantContextData? Data =>
        httpContextAccessor.HttpContext?.Items[TenantContextKeys.Key] as TenantContextData;

    public bool Resuelto => Data is not null;

    public ActorTipo Tipo => Require().Tipo;

    public string Sub => Require().Sub;

    public TenantId? TenantId => Require().TenantId;

    public IpsId? IpsId => Require().IpsId;

    private TenantContextData Require() =>
        Data ?? throw new InvalidOperationException(
            "El contexto de tenant no está resuelto. " +
            "Solo se puede acceder a ITenantContext dentro de un request autenticado " +
            "y después de que TenantContextMiddleware lo haya procesado.");
}
