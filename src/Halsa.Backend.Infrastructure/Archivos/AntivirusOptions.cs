namespace Halsa.Backend.Infrastructure.Archivos;

/// <summary>Sección <c>Antivirus</c>: dónde escucha <c>clamd</c> (servicio <c>halsa.backend.antivirus</c>).</summary>
public sealed class AntivirusOptions
{
    public const string SectionName = "Antivirus";

    public string Host { get; set; } = "localhost";

    public int Puerto { get; set; } = 3310;

    /// <summary>Lo máximo que se espera a <c>clamd</c> por un análisis completo (SPEC 07: 30 s).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
