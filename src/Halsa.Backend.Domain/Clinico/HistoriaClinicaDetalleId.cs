namespace Halsa.Backend.Domain.Clinico;

/// <summary>Identificador único de un <see cref="HistoriaClinicaDetalle"/>. Siempre UUID v7.</summary>
public readonly record struct HistoriaClinicaDetalleId(Guid Value)
{
    public static HistoriaClinicaDetalleId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
