namespace Halsa.Backend.Application.Integraciones.Canonico;

/// <summary>Formulario de la Res. 156/2005 que radicó el caso. Se guarda como <c>FURAT</c> o <c>FUREP</c>.</summary>
public enum TipoReporte
{
    Furat = 1,
    Furep = 2
}

public static class TipoReporteCodigos
{
    /// <summary>El código que se guarda en las columnas <c>tipo</c>: <c>FURAT</c> o <c>FUREP</c>.</summary>
    public static string Codigo(this TipoReporte tipo) => tipo switch
    {
        TipoReporte.Furat => "FURAT",
        TipoReporte.Furep => "FUREP",
        _ => throw new ArgumentOutOfRangeException(nameof(tipo), tipo, "Tipo de reporte desconocido.")
    };

    public static TipoReporte Parse(string codigo) => codigo switch
    {
        "FURAT" => TipoReporte.Furat,
        "FUREP" => TipoReporte.Furep,
        _ => throw new ArgumentOutOfRangeException(nameof(codigo), codigo, "Tipo de reporte desconocido.")
    };
}
