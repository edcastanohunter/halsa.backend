using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.Infrastructure.Terminos;
using Microsoft.Extensions.Logging;
using Npgsql;
using Quartz;

namespace Halsa.Backend.Infrastructure.Worker.Jobs;

/// <summary>
/// Asegura los festivos <c>CALCULADO</c> del año actual (en Colombia) y los <see cref="AniosAdelante"/> siguientes en
/// <c>sst.festivos</c> (SPEC 09). Nunca pisa una fila <c>MANUAL</c>: la fecha es la PK y el insert no hace nada ante
/// un conflicto. Si insertó alguno, dispara <c>RecalcularPlazosJob</c>, porque un festivo nuevo alarga los plazos
/// abiertos que lo cruzan.
/// </summary>
[DisallowConcurrentExecution]
internal sealed partial class FestivosJob(BaseDatosWorker baseDatos, TimeProvider time, ILogger<FestivosJob> logger) : IJob
{
    public static readonly JobKey Key = new("festivos", TrabajosWorker.Grupo);

    public const int AniosAdelante = 2;

    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        int insertados = await AsegurarAsync(context.CancellationToken);

        if (insertados > 0)
        {
            await context.Scheduler.TriggerJob(RecalcularPlazosJob.Key, context.CancellationToken);
        }
    }

    /// <summary>Inserta los que falten. Devuelve cuántos insertó.</summary>
    internal async Task<int> AsegurarAsync(CancellationToken ct)
    {
        int anio = CalculadoraTerminos.FechaLocal(time.GetUtcNow()).Year;

        Festivo[] festivos = [.. Enumerable.Range(anio, AniosAdelante + 1).SelectMany(CalendarioColombia.Festivos)];

        await using NpgsqlConnection conexion = await baseDatos.AbrirAsync(ct);
        await using NpgsqlCommand cmd = conexion.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sst.festivos (fecha, descripcion, origen)
            SELECT f.fecha, f.descripcion, @origen
            FROM unnest(@fechas, @descripciones) AS f (fecha, descripcion)
            ON CONFLICT (fecha) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("origen", FestivoFila.Calculado);
        cmd.Parameters.AddWithValue("fechas", festivos.Select(f => f.Fecha).ToArray());
        cmd.Parameters.AddWithValue("descripciones", festivos.Select(f => f.Descripcion).ToArray());

        int insertados = await cmd.ExecuteNonQueryAsync(ct);

        LogFestivos(logger, anio, anio + AniosAdelante, insertados);
        return insertados;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Festivos {Desde}-{Hasta} asegurados: {Insertados} nuevos")]
    private static partial void LogFestivos(ILogger logger, int desde, int hasta, int insertados);
}
