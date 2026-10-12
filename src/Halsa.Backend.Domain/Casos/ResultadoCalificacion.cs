namespace Halsa.Backend.Domain.Casos;

/// <summary>
/// Resultado de la calificación de origen o de pérdida de capacidad laboral (PCL).
/// Nulo hasta que el caso alcanza la fase de calificación.
/// </summary>
public sealed record ResultadoCalificacion(
    decimal? PorcentajePcl,
    DateOnly? FechaEstructuracion,
    string Origen);
