using Halsa.Backend.Domain.Incapacidades;

namespace Halsa.Backend.Application.Incapacidades;

/// <summary>
/// Sección <c>Incapacidades</c>: los umbrales de las reglas y de las alertas, pendientes de validación jurídica antes
/// de producción.
/// </summary>
public sealed class IncapacidadesOptions
{
    public const string SectionName = "Incapacidades";

    /// <summary>Días máximos entre el fin de una incapacidad y el inicio de su prórroga (D. 1427/2022).</summary>
    public int MaxDiasEntreProrrogas { get; set; } = ParametrosIncapacidad.PorDefecto.MaxDiasEntreProrrogas;

    /// <summary>A partir de cuántos días entre el inicio y la carga se alerta <c>CARGA_EXTEMPORANEA</c>.</summary>
    public int DiasCargaExtemporanea { get; set; } = ParametrosIncapacidad.PorDefecto.DiasCargaExtemporanea;

    public ParametrosIncapacidad Parametros() => new()
    {
        MaxDiasEntreProrrogas = MaxDiasEntreProrrogas,
        DiasCargaExtemporanea = DiasCargaExtemporanea
    };
}
