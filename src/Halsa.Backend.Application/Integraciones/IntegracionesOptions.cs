namespace Halsa.Backend.Application.Integraciones;

/// <summary>Sección <c>Integraciones</c>.</summary>
public sealed class IntegracionesOptions
{
    public const string SectionName = "Integraciones";

    /// <summary>
    /// Dos eventos del mismo trabajador y tipo a esta distancia o menos son el mismo evento. Un parámetro porque
    /// dos accidentes reales el mismo día existen: el webhook <c>caso.rechazado</c> lleva <c>duplicado_de</c>
    /// para que la ARL lo revise.
    /// </summary>
    public TimeSpan VentanaDuplicado { get; set; } = TimeSpan.FromHours(24);
}
