namespace Halsa.Backend.Domain.Terminos;

/// <summary>
/// Una regla de <c>sst.reglas_termino</c> (SPEC 09): global, sin tenant. Un <see cref="Codigo"/> tiene varias versiones
/// por vigencia, y a un plazo se le aplica la vigente en su <c>inicia_en</c>. Los valores son el punto de partida de
/// ARQ §3.4, pendientes de validación jurídica.
/// </summary>
public sealed class ReglaTermino
{
    // Parameterless constructor required by EF Core materializer.
    private ReglaTermino() { }

    public ReglaTermino(
        Guid id,
        string codigo,
        string eventoDisparador,
        int cantidad,
        UnidadTermino unidad,
        int alertaCantidad,
        UnidadTermino alertaUnidad,
        string baseLegal,
        DateOnly vigenteDesde,
        DateOnly? vigenteHasta)
    {
        Id = id;
        Codigo = codigo;
        EventoDisparador = eventoDisparador;
        Cantidad = cantidad;
        Unidad = unidad;
        AlertaCantidad = alertaCantidad;
        AlertaUnidad = alertaUnidad;
        BaseLegal = baseLegal;
        VigenteDesde = vigenteDesde;
        VigenteHasta = vigenteHasta;
    }

    public Guid Id { get; private set; }

    public string Codigo { get; private set; } = string.Empty;

    /// <summary><c>CasoRadicado</c>, <c>IncapacidadRegistrada</c> o <c>DictamenPclNotificado</c>.</summary>
    public string EventoDisparador { get; private set; } = string.Empty;

    public int Cantidad { get; private set; }

    public UnidadTermino Unidad { get; private set; }

    public int AlertaCantidad { get; private set; }

    public UnidadTermino AlertaUnidad { get; private set; }

    public string BaseLegal { get; private set; } = string.Empty;

    public DateOnly VigenteDesde { get; private set; }

    /// <summary>Inclusiva. Null: sin fin.</summary>
    public DateOnly? VigenteHasta { get; private set; }

    /// <summary>Si la regla aplica a un plazo que inicia en <paramref name="instante"/> (su fecha en Colombia).</summary>
    public bool VigenteEn(DateTimeOffset instante)
    {
        DateOnly fecha = CalculadoraTerminos.FechaLocal(instante);
        return VigenteDesde <= fecha && (VigenteHasta is null || fecha <= VigenteHasta);
    }

    /// <summary>El vencimiento y la alerta de un plazo de esta regla que inicia en <paramref name="iniciaEn"/>.</summary>
    public (DateTimeOffset VenceEn, DateTimeOffset AlertaEn) Calcular(CalculadoraTerminos calculadora, DateTimeOffset iniciaEn)
    {
        ArgumentNullException.ThrowIfNull(calculadora);

        DateTimeOffset venceEn = calculadora.Vencimiento(iniciaEn, Cantidad, Unidad);
        return (venceEn, calculadora.Alerta(venceEn, AlertaCantidad, AlertaUnidad));
    }
}

/// <summary>Los códigos de las reglas semilla y sus eventos disparadores.</summary>
public static class CodigosRegla
{
    public const string ReporteFurat = "REPORTE_FURAT";
    public const string ReporteFurep = "REPORTE_FUREP";
    public const string InvestigacionAt = "INVESTIGACION_AT";
    public const string RemisionInvestigacionGrave = "REMISION_INVESTIGACION_GRAVE";
    public const string ControversiaDictamen = "CONTROVERSIA_DICTAMEN";
    public const string TopeIt180 = "TOPE_IT_180";

    /// <summary>SPEC 12: la copia de la HC que pide el paciente. Pendiente de validación jurídica, como las demás.</summary>
    public const string SolicitudCopiaHc = "SOLICITUD_COPIA_HC";

    public const string CasoRadicado = "CasoRadicado";
    public const string IncapacidadRegistrada = "IncapacidadRegistrada";
    public const string DictamenPclNotificado = "DictamenPclNotificado";
    public const string SolicitudCopiaHcCreada = "SolicitudCopiaHcCreada";
}
