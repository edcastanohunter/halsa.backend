namespace Halsa.Backend.Domain.Incapacidades;

/// <summary>
/// Las alertas de una incapacidad (SPEC 07). No bloquean ni se guardan: se calculan en la respuesta de la carga y
/// en la consulta. La alerta por tiempo, sin una carga nueva, es del motor de plazos (SPEC 09).
/// </summary>
public static class AlertasIncapacidad
{
    public const string Acumulado150Dias = "ACUMULADO_150_DIAS";
    public const string Acumulado180Dias = "ACUMULADO_180_DIAS";
    public const string Acumulado540Dias = "ACUMULADO_540_DIAS";
    public const string CargaExtemporanea = "CARGA_EXTEMPORANEA";

    /// <summary>Las alertas en orden fijo: primero las de acumulado, de menor a mayor, y luego la de carga.</summary>
    /// <param name="diasAcumulados">Los días de la cadena hasta esta incapacidad, incluida.</param>
    /// <param name="fechaInicio">El inicio de esta incapacidad.</param>
    /// <param name="registradaEn">El momento de la carga; su fecha se toma en la hora de Colombia.</param>
    public static IReadOnlyList<string> Calcular(
        int diasAcumulados,
        DateOnly fechaInicio,
        DateTimeOffset registradaEn,
        ParametrosIncapacidad parametros)
    {
        ArgumentNullException.ThrowIfNull(parametros);

        var alertas = new List<string>(4);

        if (diasAcumulados >= 150)
        {
            alertas.Add(Acumulado150Dias);
        }

        if (diasAcumulados >= 180)
        {
            alertas.Add(Acumulado180Dias);
        }

        if (diasAcumulados >= 540)
        {
            alertas.Add(Acumulado540Dias);
        }

        int diasHastaLaCarga = Incapacidad.FechaColombia(registradaEn).DayNumber - fechaInicio.DayNumber;
        if (diasHastaLaCarga > parametros.DiasCargaExtemporanea)
        {
            alertas.Add(CargaExtemporanea);
        }

        return alertas;
    }
}
