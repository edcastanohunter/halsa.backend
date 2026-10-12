using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Infrastructure.Tenancy;

/// <summary>
/// No-op implementation of <see cref="ITenantContext"/> used when there is no HTTP request
/// in scope — for example, inside migration or seed contexts where the DbContext is
/// instantiated directly rather than via the DI container.
///
/// <see cref="Resuelto"/> is always <c>false</c>, so the tenant query filters and the RLS
/// policies both fail closed: no <c>atel</c> rows are visible through this context.
/// </summary>
public sealed class NullTenantContext : ITenantContext
{
    public static readonly NullTenantContext Instance = new();

    private NullTenantContext() { }

    public bool Resuelto => false;

    public ActorTipo Tipo => throw new InvalidOperationException(
        "NullTenantContext has no resolved tenant. Use ITenantContext only inside an authenticated HTTP request.");

    public string Sub => throw new InvalidOperationException(
        "NullTenantContext has no resolved tenant. Use ITenantContext only inside an authenticated HTTP request.");

    public TenantId? TenantId => null;

    public IpsId? IpsId => null;
}
