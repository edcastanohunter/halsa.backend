using Halsa.Backend.Infrastructure.Observabilidad;
using Halsa.Backend.Infrastructure.Outbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Quartz;

namespace Halsa.Backend.Infrastructure.Worker.Jobs;

/// <summary>
/// SPEC 13: cada 5 minutos, como <c>halsa_worker</c>, cuenta lo que exige una persona y lo deja en el log con la
/// propiedad <c>Alerta</c>, que Seq convierte en un correo (runbook 07):
/// <list type="bullet">
///   <item><see cref="Alertas.OutboxAgotado"/>: mensajes del outbox sin procesar con <c>attempts &gt;= MaxAttempts</c>;
///   ya nadie los reintenta.</item>
///   <item><see cref="Alertas.WebhookDeadLetter"/>: entregas que pasaron a <c>DEAD_LETTER</c> desde la última pasada.</item>
///   <item><see cref="Alertas.WebhookSuspendida"/>: suscripciones <c>SUSPENDIDA</c> (siguen avisando hasta que alguien
///   las reactive o las borre).</item>
/// </list>
/// El sello sin publicar (<see cref="Alertas.SelloSinPublicar"/>) llega con la SPEC 11.
/// </summary>
[DisallowConcurrentExecution]
internal sealed partial class SenalesOperativasJob(
    BaseDatosWorker baseDatos,
    IOptions<OutboxOptions> outbox,
    TimeProvider time,
    ILogger<SenalesOperativasJob> logger) : IJob
{
    public static readonly JobKey Key = new("senales-operativas", TrabajosWorker.Grupo);

    /// <summary>Cada cuánto corre: también la ventana de las entregas nuevas en <c>DEAD_LETTER</c>.</summary>
    public static readonly TimeSpan Periodo = TimeSpan.FromMinutes(5);

    public Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return RevisarAsync(context.CancellationToken);
    }

    internal async Task<SenalesOperativas> RevisarAsync(CancellationToken ct)
    {
        await using NpgsqlConnection conexion = await baseDatos.AbrirAsync(ct);
        await using NpgsqlCommand cmd = conexion.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT count(*) FROM outbox.outbox_messages
                  WHERE processed_on_utc IS NULL AND attempts >= @max_intentos),
                (SELECT count(*) FROM integracion.entregas_webhook
                  WHERE estado = 'DEAD_LETTER' AND proximo_intento_en >= @desde),
                (SELECT count(*) FROM integracion.suscripciones_webhook WHERE estado = 'SUSPENDIDA')
            """;
        cmd.Parameters.AddWithValue("max_intentos", outbox.Value.MaxAttempts);
        cmd.Parameters.AddWithValue("desde", time.GetUtcNow() - Periodo);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var senales = new SenalesOperativas(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));

        if (senales.OutboxAgotados > 0)
        {
            LogOutbox(logger, Alertas.OutboxAgotado, senales.OutboxAgotados, outbox.Value.MaxAttempts);
        }

        if (senales.EntregasDeadLetter > 0)
        {
            LogDeadLetter(logger, Alertas.WebhookDeadLetter, senales.EntregasDeadLetter);
        }

        if (senales.SuscripcionesSuspendidas > 0)
        {
            LogSuspendidas(logger, Alertas.WebhookSuspendida, senales.SuscripcionesSuspendidas);
        }

        return senales;
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Alerta {Alerta}: {Cantidad} mensaje(s) del outbox agotaron sus {MaxIntentos} intentos y ya no se reintentan")]
    private static partial void LogOutbox(ILogger logger, string alerta, long cantidad, int maxIntentos);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Alerta {Alerta}: {Cantidad} entrega(s) de webhook pasaron a DEAD_LETTER")]
    private static partial void LogDeadLetter(ILogger logger, string alerta, long cantidad);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Alerta {Alerta}: {Cantidad} suscripción(es) de webhook están SUSPENDIDA")]
    private static partial void LogSuspendidas(ILogger logger, string alerta, long cantidad);
}

internal sealed record SenalesOperativas(long OutboxAgotados, long EntregasDeadLetter, long SuscripcionesSuspendidas);
