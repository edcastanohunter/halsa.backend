using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.IntegrationTests.Abstractions;

/// <summary>A resolved tenant context for tests that need the RLS session variables set.</summary>
internal sealed class FakeTenantContext(ActorTipo tipo, TenantId? tenantId, IpsId? ipsId, string? sub = null) : ITenantContext
{
    public const string SubPorDefecto = "01999000-0000-7000-8000-00000000f001";

    public bool Resuelto => true;

    public ActorTipo Tipo => tipo;

    public string Sub => sub ?? SubPorDefecto;

    public TenantId? TenantId => tenantId;

    public IpsId? IpsId => ipsId;

    public static FakeTenantContext Arl(string tenantId) => new(ActorTipo.Arl, new TenantId(tenantId), null);

    public static FakeTenantContext Ips(string ipsId) => new(ActorTipo.Ips, null, new IpsId(ipsId));

    /// <summary>A patient of <c>halsa-pacientes</c> (SPEC 12): no tenant, no IPS, only its <c>sub</c>.</summary>
    public static FakeTenantContext Paciente(string sub) => new(ActorTipo.Paciente, null, null, sub);
}
