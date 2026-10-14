namespace Halsa.Backend.Domain.Terminos;

/// <summary>Un día no hábil: la fecha y su nombre.</summary>
public sealed record Festivo(DateOnly Fecha, string Descripcion);

/// <summary>
/// Los festivos nacionales de Colombia de un año, calculados (SPEC 09). Se guardan en <c>sst.festivos</c> como
/// <c>CALCULADO</c>, y una fila <c>MANUAL</c> corrige lo que un decreto cambie: un olvido al cargarlos a mano cada año
/// corrompería en silencio todos los plazos.
/// <list type="bullet">
///   <item>Fijos: 1 ene, 1 may, 20 jul, 7 ago, 8 dic y 25 dic.</item>
///   <item>Trasladables al lunes siguiente (Ley 51/1983): 6 ene, 19 mar, 29 jun, 15 ago, 12 oct, 1 nov y 11 nov.</item>
///   <item>Por la Pascua: Jueves y Viernes Santo, y Ascensión, Corpus Christi y Sagrado Corazón, trasladados al lunes.</item>
/// </list>
/// </summary>
public static class CalendarioColombia
{
    /// <summary>
    /// Los festivos de <paramref name="anio"/>, ordenados por fecha: 18, salvo cuando dos caen el mismo lunes (en 2025,
    /// San Pedro y el Sagrado Corazón, el 30 de junio), que salen en una sola fila con ambos nombres.
    /// </summary>
    public static IReadOnlyList<Festivo> Festivos(int anio)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(anio, 1984);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(anio, 9999);

        DateOnly pascua = DomingoDePascua(anio);

        List<Festivo> festivos =
        [
            new(new DateOnly(anio, 1, 1), "Año Nuevo"),
            new(new DateOnly(anio, 5, 1), "Día del Trabajo"),
            new(new DateOnly(anio, 7, 20), "Día de la Independencia"),
            new(new DateOnly(anio, 8, 7), "Batalla de Boyacá"),
            new(new DateOnly(anio, 12, 8), "Inmaculada Concepción"),
            new(new DateOnly(anio, 12, 25), "Navidad"),

            new(AlLunes(new DateOnly(anio, 1, 6)), "Día de los Reyes Magos"),
            new(AlLunes(new DateOnly(anio, 3, 19)), "Día de San José"),
            new(AlLunes(new DateOnly(anio, 6, 29)), "San Pedro y San Pablo"),
            new(AlLunes(new DateOnly(anio, 8, 15)), "La Asunción de la Virgen"),
            new(AlLunes(new DateOnly(anio, 10, 12)), "Día de la Raza"),
            new(AlLunes(new DateOnly(anio, 11, 1)), "Todos los Santos"),
            new(AlLunes(new DateOnly(anio, 11, 11)), "Independencia de Cartagena"),

            new(pascua.AddDays(-3), "Jueves Santo"),
            new(pascua.AddDays(-2), "Viernes Santo"),
            new(AlLunes(pascua.AddDays(39)), "Ascensión del Señor"),
            new(AlLunes(pascua.AddDays(60)), "Corpus Christi"),
            new(AlLunes(pascua.AddDays(68)), "Sagrado Corazón de Jesús")
        ];

        // sst.festivos is keyed by date.
        return [.. festivos
            .GroupBy(f => f.Fecha)
            .OrderBy(g => g.Key)
            .Select(g => new Festivo(g.Key, string.Join(" / ", g.Select(f => f.Descripcion))))];
    }

    /// <summary>El domingo de Pascua (calendario gregoriano), por el algoritmo anónimo de Meeus/Jones/Butcher.</summary>
    public static DateOnly DomingoDePascua(int anio)
    {
        int a = anio % 19;
        int b = anio / 100;
        int c = anio % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = ((19 * a) + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        int m = (a + (11 * h) + (22 * l)) / 451;
        int mes = (h + l - (7 * m) + 114) / 31;
        int dia = ((h + l - (7 * m) + 114) % 31) + 1;

        return new DateOnly(anio, mes, dia);
    }

    /// <summary>Ley 51/1983: si no cae en lunes, pasa al lunes siguiente.</summary>
    private static DateOnly AlLunes(DateOnly fecha)
    {
        int dias = ((int)DayOfWeek.Monday - (int)fecha.DayOfWeek + 7) % 7;
        return fecha.AddDays(dias);
    }
}
