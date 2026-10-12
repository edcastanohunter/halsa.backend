using Halsa.Backend.SharedKernel.Primitives;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Domain.Casos;

/// <summary>
/// Envío de un caso a una IPS para que lo gestione clínicamente.
/// Las remisiones nunca se borran: revocar marca <see cref="RevocadaEn"/>.
/// </summary>
public sealed class Remision : Entity<RemisionId>
{
    // Parameterless constructor required by EF Core materializer.
    private Remision() { }

    internal Remision(
        RemisionId id,
        CasoMedicoId casoId,
        TenantId tenantId,
        IpsId ipsId,
        string remitidoPor,
        DateTimeOffset remitidoEn)
        : base(id)
    {
        CasoMedicoId = casoId;
        TenantId = tenantId;
        IpsId = ipsId;
        RemitidoPor = remitidoPor;
        RemitidoEn = remitidoEn;
    }

    public CasoMedicoId CasoMedicoId { get; private set; }

    /// <summary>Copia del tenant del caso, para que la política RLS de la tabla no necesite un join.</summary>
    public TenantId TenantId { get; private set; }

    public IpsId IpsId { get; private set; }

    public DateTimeOffset RemitidoEn { get; private set; }

    public string RemitidoPor { get; private set; } = string.Empty;

    public DateTimeOffset? RevocadaEn { get; private set; }

    public string? RevocadaPor { get; private set; }

    /// <summary>Una remisión es vigente mientras no ha sido revocada.</summary>
    public bool Vigente => RevocadaEn is null;

    /// <summary>Revocar la remisión — llamado únicamente desde <see cref="CasoMedico"/>.</summary>
    internal void Revocar(string revocadaPor, DateTimeOffset ahora)
    {
        RevocadaEn = ahora;
        RevocadaPor = revocadaPor;
    }
}
