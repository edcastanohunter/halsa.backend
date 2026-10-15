using Halsa.Backend.Infrastructure.Database;
using Microsoft.Extensions.Configuration;

namespace Halsa.Backend.Infrastructure.Proceso;

/// <summary>
/// <c>Halsa:Rol=Migrador</c> (SPEC 13): las migraciones son un paso del despliegue, no del arranque de la API. Aplica las
/// de los dos contextos con la cadena <c>Migrations</c> (<c>halsa_owner</c>), asegura las particiones de auditoría y
/// devuelve el código de salida del proceso: <c>0</c> si todo quedó aplicado (también cuando no había nada pendiente) y
/// <c>1</c> si algo falla, para que <c>desplegar.sh</c> no levante la versión nueva.
/// </summary>
public static class Migrador
{
    public const int Exito = 0;

    public const int Fallo = 1;

    public static async Task<int> EjecutarAsync(IConfiguration configuration, TextWriter salida, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(salida);

        string? cadena = configuration.GetConnectionString("Migrations");

        if (string.IsNullOrWhiteSpace(cadena))
        {
            await salida.WriteLineAsync("Migrador: la cadena de conexión 'Migrations' (halsa_owner) no está configurada.");
            return Fallo;
        }

        try
        {
            await DatabaseMigrator.MigrateAsync(cadena, ct);
            await DatabaseMigrator.AsegurarParticionesAsync(cadena, ct);
        }
#pragma warning disable CA1031 // Any failure is an exit code: the deploy script reads it, not a stack trace.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            await salida.WriteLineAsync($"Migrador: falló la migración. {ex}");
            return Fallo;
        }

        await salida.WriteLineAsync(
            $"Migrador: migraciones aplicadas y particiones de auditoría aseguradas ({DatabaseMigrator.MesesDeParticionesAdelante} meses).");
        return Exito;
    }
}
