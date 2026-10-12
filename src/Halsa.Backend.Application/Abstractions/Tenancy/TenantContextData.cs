using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Abstractions.Tenancy;

/// <summary>
/// Resultado inmutable que produce <c>TenantContextResolver</c> al procesar el token.
/// El middleware lo almacena en <c>HttpContext.Items</c> para que
/// <c>HttpTenantContext</c> lo lea durante el mismo request.
/// </summary>
public sealed record TenantContextData(
    ActorTipo Tipo,
    string Sub,
    TenantId? TenantId,
    IpsId? IpsId);
