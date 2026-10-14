using Halsa.Backend.Infrastructure.Archivos;
using Halsa.Backend.Infrastructure.Database;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Quartz;

namespace Halsa.Backend.Infrastructure.Worker.Jobs;

/// <summary>
/// Mantiene creadas las particiones mensuales de <c>auditoria.accesos</c> (SPEC 04): el mes actual y los
/// <see cref="DatabaseMigrator.MesesDeParticionesAdelante"/> siguientes. La función es <c>SECURITY DEFINER</c>, así que
/// <c>halsa_worker</c> la ejecuta sin ser dueño de la tabla.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class ParticionesAuditoriaJob(BaseDatosWorker baseDatos) : IJob
{
    public static readonly JobKey Key = new("particiones-auditoria", TrabajosWorker.Grupo);

    public Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return AsegurarAsync(context.CancellationToken);
    }

    internal async Task AsegurarAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conexion = await baseDatos.AbrirAsync(ct);
        await using NpgsqlCommand cmd = conexion.CreateCommand();
        cmd.CommandText = "SELECT auditoria.asegurar_particiones(@meses)";
        cmd.Parameters.AddWithValue("meses", DatabaseMigrator.MesesDeParticionesAdelante);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Borra las llaves de idempotencia vencidas de la ingesta (SPEC 05). La política de <c>halsa_worker</c> solo le deja ver
/// las de <c>expira_en &lt; now()</c>: una vigente no se puede borrar ni por error.
/// </summary>
[DisallowConcurrentExecution]
internal sealed partial class PurgaIdempotenciaJob(BaseDatosWorker baseDatos, ILogger<PurgaIdempotenciaJob> logger) : IJob
{
    public static readonly JobKey Key = new("purga-idempotencia", TrabajosWorker.Grupo);

    public Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return PurgarAsync(context.CancellationToken);
    }

    /// <summary>Devuelve cuántas llaves borró.</summary>
    internal async Task<int> PurgarAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conexion = await baseDatos.AbrirAsync(ct);
        await using NpgsqlCommand cmd = conexion.CreateCommand();
        cmd.CommandText = "DELETE FROM integracion.solicitudes_idempotentes WHERE expira_en < now()";

        int borradas = await cmd.ExecuteNonQueryAsync(ct);
        if (borradas > 0)
        {
            LogPurga(logger, borradas);
        }

        return borradas;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Llaves de idempotencia vencidas borradas: {Borradas}")]
    private static partial void LogPurga(ILogger logger, int borradas);
}

/// <summary>
/// Borra de <c>Archivos:Raiz</c> los archivos de más de <see cref="Antiguedad"/> sin fila en <c>clinico.soportes</c>
/// (SPEC 07): los que dejó una carga cuya transacción falló después de escribirlos, y los temporales abandonados. Nunca
/// borra un archivo con metadata, y el umbral está muy por encima de lo que dura una carga en curso.
/// </summary>
[DisallowConcurrentExecution]
internal sealed partial class ArchivosHuerfanosJob(
    BaseDatosWorker baseDatos,
    IOptions<ArchivosOptions> options,
    TimeProvider time,
    ILogger<ArchivosHuerfanosJob> logger,
    IHostEnvironment? environment = null)
    : IJob
{
    public static readonly JobKey Key = new("archivos-huerfanos", TrabajosWorker.Grupo);

    public static readonly TimeSpan Antiguedad = TimeSpan.FromHours(1);

    private const int Lote = 500;

    // The same resolution as LocalEncryptedFileStore, so both look at the same folder.
    private readonly string _raiz = Path.GetFullPath(
        options.Value.Raiz, environment?.ContentRootPath ?? Directory.GetCurrentDirectory());

    public Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return BorrarAsync(context.CancellationToken);
    }

    /// <summary>Devuelve cuántos archivos borró.</summary>
    internal async Task<int> BorrarAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_raiz))
        {
            return 0;
        }

        DateTime limite = time.GetUtcNow().UtcDateTime - Antiguedad;

        // Relative paths with '/', as LocalEncryptedFileStore records them in clinico.soportes.ruta.
        Dictionary<string, string> viejos = Directory
            .EnumerateFiles(_raiz, "*", SearchOption.AllDirectories)
            .Where(archivo => File.GetLastWriteTimeUtc(archivo) < limite)
            .ToDictionary(archivo => Path.GetRelativePath(_raiz, archivo).Replace('\\', '/'), StringComparer.Ordinal);

        int borrados = 0;
        await using NpgsqlConnection conexion = await baseDatos.AbrirAsync(ct);

        foreach (string[] lote in viejos.Keys.Chunk(Lote))
        {
            HashSet<string> conMetadata = await ConMetadataAsync(conexion, lote, ct);

            foreach (string ruta in lote.Where(r => !conMetadata.Contains(r)))
            {
                File.Delete(viejos[ruta]);
                borrados++;
            }
        }

        if (borrados > 0)
        {
            LogBorrados(logger, borrados);
        }

        return borrados;
    }

    private static async Task<HashSet<string>> ConMetadataAsync(NpgsqlConnection conexion, string[] rutas, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = conexion.CreateCommand();
        cmd.CommandText = "SELECT ruta FROM clinico.soportes WHERE ruta = ANY(@rutas)";
        cmd.Parameters.AddWithValue("rutas", rutas);

        var conMetadata = new HashSet<string>(StringComparer.Ordinal);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            conMetadata.Add(reader.GetString(0));
        }

        return conMetadata;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Archivos huérfanos borrados: {Borrados}")]
    private static partial void LogBorrados(ILogger logger, int borrados);
}
