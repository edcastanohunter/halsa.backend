namespace Halsa.Backend.Infrastructure.Clinico.Crypto;

/// <summary>
/// Sección <c>Clinico</c>. La KEK son 32 bytes en base64, en un archivo (<see cref="KekPath"/>) o,
/// solo en Development y tests, en línea (<see cref="KekBase64"/>, desde <c>dotnet user-secrets</c>).
/// </summary>
public sealed class ClinicoOptions
{
    public const string SectionName = "Clinico";

    public string KekId { get; set; } = "local:v1";

    /// <summary>Producción (VPS): un secreto de Docker, por ejemplo <c>/run/secrets/halsa_kek</c>.</summary>
    public string? KekPath { get; set; }

    /// <summary>Solo Development y tests. En Production el arranque falla si tiene valor.</summary>
    public string? KekBase64 { get; set; }
}
