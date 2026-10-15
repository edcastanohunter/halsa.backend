using Halsa.Backend.Infrastructure.Worker;
using Microsoft.Extensions.Logging;
using Npgsql;
using Quartz;

namespace Halsa.Backend.Infrastructure.Webhooks;

/// <summary>
/// Borra a diario las entregas <c>ENTREGADA</c> de más de <see cref="Retencion"/> (SPEC 10), como <c>halsa_worker</c>. Las
/// pendientes, en dead letter y descartadas se quedan: son las que alguien puede querer reintentar.
/// </summary>
[DisallowConcurrentExecution]
internal sealed partial class PurgaEntregasWebhookJob(
    BaseDatosWorker baseDatos,
    TimeProvider time,
    ILogger<PurgaEntregasWebhookJob> logger)
    : IJob
{
    public static readonly JobKey Key = new("purga-entregas-webhook", TrabajosWorker.Grupo);

    public static readonly TimeSpan Retencion = TimeSpan.FromDays(90);

    public Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return PurgarAsync(context.CancellationToken);
    }

    /// <summary>Devuelve cuántas entregas borró.</summary>
    internal async Task<int> PurgarAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conexion = await baseDatos.AbrirAsync(ct);
        await using NpgsqlCommand cmd = conexion.CreateCommand();
        cmd.CommandText = "DELETE FROM integracion.entregas_webhook WHERE estado = 'ENTREGADA' AND entregada_en < @limite";
        cmd.Parameters.AddWithValue("limite", time.GetUtcNow().ToUniversalTime() - Retencion);

        int borradas = await cmd.ExecuteNonQueryAsync(ct);
        if (borradas > 0)
        {
            LogPurga(logger, borradas);
        }

        return borradas;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Entregas de webhooks de más de 90 días borradas: {Borradas}")]
    private static partial void LogPurga(ILogger logger, int borradas);
}
