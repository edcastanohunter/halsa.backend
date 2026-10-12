using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Domain.Casos;

/// <summary>
/// Asignación de un usuario del sistema (identificado por <c>sub</c>) a un caso médico.
/// La clave natural es <c>(CasoMedicoId, Sub)</c>; la operación es idempotente.
/// </summary>
public sealed class Asignacion
{
    // Parameterless constructor required by EF Core materializer.
    private Asignacion() { }

    internal Asignacion(
        CasoMedicoId casoMedicoId,
        TenantId tenantId,
        string sub,
        string asignadoPor,
        DateTimeOffset asignadoEn)
    {
        CasoMedicoId = casoMedicoId;
        TenantId = tenantId;
        Sub = sub;
        AsignadoPor = asignadoPor;
        AsignadoEn = asignadoEn;
    }

    public CasoMedicoId CasoMedicoId { get; private set; }

    /// <summary>Copia del tenant del caso, para que la política RLS de la tabla no necesite un join.</summary>
    public TenantId TenantId { get; private set; }

    public string Sub { get; private set; } = string.Empty;

    public DateTimeOffset AsignadoEn { get; private set; }

    public string AsignadoPor { get; private set; } = string.Empty;
}
