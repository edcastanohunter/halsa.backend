namespace Halsa.Backend.Application.Abstractions.Archivos;

/// <summary>
/// Analiza un archivo antes de guardarlo (SPEC 07). Es síncrono y falla cerrado: si el antivirus no responde, el
/// resultado es <see cref="ResultadoAntivirus.NoDisponible"/> y el archivo no se acepta.
/// </summary>
public interface IAntivirusScanner
{
    /// <summary>Lee <paramref name="contenido"/> desde su posición actual hasta el final. No lanza por el antivirus.</summary>
    Task<ResultadoAntivirus> AnalizarAsync(Stream contenido, CancellationToken ct);
}

/// <summary>El veredicto del antivirus.</summary>
public abstract record ResultadoAntivirus
{
    private ResultadoAntivirus() { }

    public static readonly ResultadoAntivirus Limpio = new LimpioResultado();

    public static ResultadoAntivirus Infectado(string firma) => new InfectadoResultado(firma);

    public static ResultadoAntivirus NoDisponible(string motivo) => new NoDisponibleResultado(motivo);

    /// <summary>El archivo no tiene ninguna firma conocida.</summary>
    public sealed record LimpioResultado : ResultadoAntivirus;

    /// <summary>El antivirus encontró <paramref name="Firma"/> (por ejemplo <c>Eicar-Test-Signature</c>).</summary>
    public sealed record InfectadoResultado(string Firma) : ResultadoAntivirus;

    /// <summary>El antivirus no respondió, respondió un error o tardó demasiado. No se sabe si el archivo es seguro.</summary>
    public sealed record NoDisponibleResultado(string Motivo) : ResultadoAntivirus;
}
