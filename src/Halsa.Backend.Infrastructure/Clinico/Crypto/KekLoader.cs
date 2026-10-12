namespace Halsa.Backend.Infrastructure.Clinico.Crypto;

/// <summary>Lee la KEK de <see cref="ClinicoOptions"/>. Lo comparten el validador de arranque y el proveedor.</summary>
internal static class KekLoader
{
    public const int KekBytes = 32;

    /// <summary>
    /// <see cref="ClinicoOptions.KekBase64"/> tiene prioridad sobre <see cref="ClinicoOptions.KekPath"/>.
    /// El validador ya impide que <c>KekBase64</c> llegue a Production.
    /// </summary>
    public static bool TryLoad(ClinicoOptions options, out byte[] kek, out string error)
    {
        kek = [];

        string? base64;
        string origen;

        if (!string.IsNullOrWhiteSpace(options.KekBase64))
        {
            base64 = options.KekBase64;
            origen = "Clinico:KekBase64";
        }
        else if (!string.IsNullOrWhiteSpace(options.KekPath))
        {
            if (!File.Exists(options.KekPath))
            {
                error = $"Clinico:KekPath '{options.KekPath}' no existe.";
                return false;
            }

            base64 = File.ReadAllText(options.KekPath);
            origen = $"Clinico:KekPath '{options.KekPath}'";
        }
        else
        {
            error = "No hay KEK: configure Clinico:KekPath (o Clinico:KekBase64 en Development).";
            return false;
        }

        try
        {
            kek = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException)
        {
            error = $"{origen} no es base64 válido.";
            return false;
        }

        if (kek.Length != KekBytes)
        {
            error = $"{origen} debe contener {KekBytes} bytes; contiene {kek.Length}.";
            kek = [];
            return false;
        }

        error = string.Empty;
        return true;
    }
}
