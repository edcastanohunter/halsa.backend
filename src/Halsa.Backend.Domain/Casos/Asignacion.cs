using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Domain.Casos;

/// <summary>
/// Asignación de un usuario del sistema (identificado por <c>sub</c>) a un caso médico.
/// La clave natural es <c>(CasoMedicoId, Sub)</c>; la operación es idempotente.
///
/// <see cref="IpsId"/> es la IPS de quien asignó (SPEC 08): <c>null</c> para el personal de la ARL. Un <c>sub</c>
/// asignado por una IPS solo da acceso clínico con un token de esa misma IPS.
/// </summary>
public sealed class Asignacion
{
    // Parameterless constructor required by EF Core materializer.
    private Asignacion() { }

    internal Asignacion(
        CasoMedicoId casoMedicoId,
        TenantId tenantId,
        string sub,
        IpsId? ipsId,
        string asignadoPor,
        DateTimeOffset asignadoEn)
    {
        CasoMedicoId = casoMedicoId;
        TenantId = tenantId;
        Sub = sub;
        IpsId = ipsId;
        AsignadoPor = asignadoPor;
        AsignadoEn = asignadoEn;
    }

    public CasoMedicoId CasoMedicoId { get; private set; }

    /// <summary>Copia del tenant del caso, para que la política RLS de la tabla no necesite un join.</summary>
    public TenantId TenantId { get; private set; }

    public string Sub { get; private set; } = string.Empty;

    /// <summary>La IPS que hizo la asignación, o <c>null</c> si la hizo la ARL.</summary>
    public IpsId? IpsId { get; private set; }

    public DateTimeOffset AsignadoEn { get; private set; }

    public string AsignadoPor { get; private set; } = string.Empty;
}
