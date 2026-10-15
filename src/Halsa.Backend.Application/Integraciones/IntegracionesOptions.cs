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

    /// <summary>
    /// SPEC 13: un actor <c>Integracion</c> debe presentar, por el proxy de confianza, el certificado cliente al que está
    /// ligado su token (<c>cnf.x5t#S256</c>, RFC 8705). Apagado en desarrollo para depurar sin certificados;
    /// <c>ProductionGuard</c> exige <c>true</c> en Production.
    /// </summary>
    public bool ExigirCertificado { get; set; }
}
