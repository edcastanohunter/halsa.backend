namespace Halsa.Backend.Domain.Terminos;

/// <summary>Cómo se cuenta un término. Se guarda como <c>HABILES</c>, <c>CALENDARIO</c> u <c>HORAS</c>.</summary>
public enum UnidadTermino
{
    Habiles,
    Calendario,
    Horas
}

/// <summary>
/// Calcula vencimientos y alertas de los términos de ley (SPEC 09) sobre el calendario laboral colombiano: sin sábados,
/// domingos ni los festivos que recibe (los de <c>sst.festivos</c>).
/// <list type="bullet">
///   <item>La zona es <c>America/Bogota</c>: UTC−5 fijo, sin horario de verano desde 1993 (como el resto del dominio).</item>
///   <item>Un término en días vence al final del día local (23:59:59) del último día que cuenta.</item>
///   <item><see cref="UnidadTermino.Habiles"/> cuenta desde el día hábil siguiente al inicio; <see cref="UnidadTermino.Calendario"/>,
///   desde el día siguiente; <see cref="UnidadTermino.Horas"/> suma horas exactas.</item>
/// </list>
/// </summary>
public sealed class CalculadoraTerminos
{
    /// <summary>America/Bogota.</summary>
    public static readonly TimeSpan ZonaColombia = TimeSpan.FromHours(-5);

    private static readonly TimeOnly FinDeDia = new(23, 59, 59);

    private readonly HashSet<DateOnly> _festivos;

    public CalculadoraTerminos(IEnumerable<DateOnly> festivos)
    {
        ArgumentNullException.ThrowIfNull(festivos);

        _festivos = [.. festivos];
    }

    /// <summary>Cuándo vence un término de <paramref name="cantidad"/> <paramref name="unidad"/> que inicia en <paramref name="iniciaEn"/>.</summary>
    public DateTimeOffset Vencimiento(DateTimeOffset iniciaEn, int cantidad, UnidadTermino unidad)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cantidad);

        return unidad switch
        {
            UnidadTermino.Habiles => FinDelDia(SumarHabiles(FechaLocal(iniciaEn), cantidad)),
            UnidadTermino.Calendario => FinDelDia(FechaLocal(iniciaEn).AddDays(cantidad)),
            UnidadTermino.Horas => iniciaEn.ToOffset(ZonaColombia).AddHours(cantidad),
            _ => throw new ArgumentOutOfRangeException(nameof(unidad), unidad, null)
        };
    }

    /// <summary>
    /// <c>alerta_en = vence_en − alerta previa</c>, en la unidad de la alerta: <paramref name="cantidad"/> días hábiles
    /// antes se queda a la misma hora local, en el día hábil que corresponde.
    /// </summary>
    public DateTimeOffset Alerta(DateTimeOffset venceEn, int cantidad, UnidadTermino unidad)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cantidad);

        DateTimeOffset local = venceEn.ToOffset(ZonaColombia);

        return unidad switch
        {
            UnidadTermino.Habiles => new DateTimeOffset(
                RestarHabiles(DateOnly.FromDateTime(local.DateTime), cantidad).ToDateTime(TimeOnly.FromDateTime(local.DateTime)),
                ZonaColombia),
            UnidadTermino.Calendario => local.AddDays(-cantidad),
            UnidadTermino.Horas => local.AddHours(-cantidad),
            _ => throw new ArgumentOutOfRangeException(nameof(unidad), unidad, null)
        };
    }

    /// <summary>Ni sábado, ni domingo, ni festivo.</summary>
    public bool EsHabil(DateOnly fecha) =>
        fecha.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !_festivos.Contains(fecha);

    /// <summary>La fecha en Colombia de un instante.</summary>
    public static DateOnly FechaLocal(DateTimeOffset instante) =>
        DateOnly.FromDateTime(instante.ToOffset(ZonaColombia).DateTime);

    /// <summary>23:59:59 de <paramref name="fecha"/> en Colombia.</summary>
    public static DateTimeOffset FinDelDia(DateOnly fecha) => new(fecha.ToDateTime(FinDeDia), ZonaColombia);

    private DateOnly SumarHabiles(DateOnly desde, int habiles)
    {
        DateOnly dia = desde;
        for (int contados = 0; contados < habiles;)
        {
            dia = dia.AddDays(1);
            if (EsHabil(dia))
            {
                contados++;
            }
        }

        return dia;
    }

    private DateOnly RestarHabiles(DateOnly desde, int habiles)
    {
        DateOnly dia = desde;
        for (int contados = 0; contados < habiles;)
        {
            dia = dia.AddDays(-1);
            if (EsHabil(dia))
            {
                contados++;
            }
        }

        return dia;
    }
}
