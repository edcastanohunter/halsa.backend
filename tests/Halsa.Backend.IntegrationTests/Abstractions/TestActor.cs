using Halsa.Backend.Application.Abstractions.Authentication;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Infrastructure.Tenancy;

namespace Halsa.Backend.IntegrationTests.Abstractions;

/// <summary>The actor of a test scope. <see cref="DatabaseFixture.CreateScope(ITenantContext)"/> sets it.</summary>
internal sealed class TestActor
{
    public ITenantContext Tenant { get; set; } = NullTenantContext.Instance;
}

/// <summary>The identity of the scope's <see cref="TestActor"/>: its <c>sub</c> as the user id.</summary>
internal sealed class TestCurrentUser(TestActor actor) : ICurrentUser
{
    public bool IsAuthenticated => actor.Tenant.Resuelto;

    public Guid UserId => IsAuthenticated && Guid.TryParse(actor.Tenant.Sub, out Guid id) ? id : Guid.Empty;

    public string? Email => null;

    public IReadOnlyCollection<string> Permissions => [];

    public bool HasPermission(string permission) => false;
}
