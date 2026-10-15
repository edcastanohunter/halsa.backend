using System.Globalization;
using System.Net;
using Halsa.Backend.Domain.Webhooks;
using Halsa.Backend.Infrastructure.Worker;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Halsa.Backend.Infrastructure.Webhooks;

/// <summary>Lo que hizo una vuelta del dispatcher.</summary>
internal sealed record ResultadoEnvio(int Entregadas, int Fallidas, int DeadLetter, int Descartadas)
{
    public int Total => Entregadas + Fallidas + DeadLetter + Descartadas;
}

/// <summary>
/// El envío de los webhooks (SPEC 10): un hosted service del Worker que da una vuelta cada
/// <see cref="WebhooksOptions.IntervaloSegundos"/> como <c>halsa_worker</c>.
///
/// Cada vuelta toma hasta <see cref="WebhooksOptions.Lote"/> entregas pendientes y vencidas de suscripciones activas con
/// <c>FOR UPDATE SKIP LOCKED</c> y las <em>arrienda</em> (corre su <c>proximo_intento_en</c> <see cref="Arriendo"/>) en
/// la misma sentencia, y confirma: dos Workers nunca toman la misma entrega, y ninguna transacción queda abierta mientras
/// se espera a un receptor. Envía con <see cref="WebhooksOptions.MaxParalelo"/> a la vez y guarda cada resultado:
/// <list type="bullet">
///   <item><c>2xx</c>: <c>ENTREGADA</c>, y la suscripción vuelve a <c>fallos_consecutivos = 0</c>.</item>
///   <item><c>410 Gone</c>: <c>DESCARTADA</c>, y la suscripción <c>SUSPENDIDA</c>.</item>
///   <item>Otro código (también un <c>3xx</c>: no se siguen), timeout o error de red: reintento con
///   <see cref="EntregaWebhook.Espera"/>, o <c>DEAD_LETTER</c> pasadas 24 h del primer intento.</item>
/// </list>
/// Todo fallo suma un <c>fallos_consecutivos</c>; al llegar a 50, la suscripción queda <c>SUSPENDIDA</c>. Si el Worker cae a
/// mitad de un envío, la entrega vuelve sola al vencer el arriendo: la entrega es <em>at-least-once</em>.
/// </summary>
internal sealed partial class WebhookDispatcherJob(
    BaseDatosWorker baseDatos,
    SafeWebhookHttpClient http,
    WebhookSecretProtector secretos,
    IOptions<WebhooksOptions> options,
    TimeProvider time,
    ILogger<WebhookDispatcherJob> logger)
    : BackgroundService
{
    /// <summary>Cuánto se reserva una entrega tomada: muy por encima de lo que dura una vuelta con los timeouts.</summary>
    public static readonly TimeSpan Arriendo = TimeSpan.FromMinutes(5);

    public const string HeaderEventId = "Halsa-Event-Id";
    public const string HeaderEventType = "Halsa-Event-Type";
    public const string HeaderDeliveryAttempt = "Halsa-Delivery-Attempt";

    private readonly WebhooksOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.IntervaloSegundos), time);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                // A full batch means more is waiting: go on without waiting for the next tick.
                while (await ProcesarLoteAsync(stoppingToken).ConfigureAwait(false) is { Total: var n } && n == _options.Lote)
                {
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // The dispatcher must survive any single bad batch.
            catch (Exception exception)
            {
                LogLoteFallido(logger, exception);
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>Una vuelta: toma, envía y guarda hasta <see cref="WebhooksOptions.Lote"/> entregas.</summary>
    internal async Task<ResultadoEnvio> ProcesarLoteAsync(CancellationToken ct)
    {
        // Npgsql writes timestamptz only from UTC offsets.
        DateTimeOffset ahora = time.GetUtcNow().ToUniversalTime();

        List<Tomada> tomadas = await TomarAsync(ahora, ct);
        if (tomadas.Count == 0)
        {
            return new ResultadoEnvio(0, 0, 0, 0);
        }

        var resultados = new Desenlace[tomadas.Count];
        await Parallel.ForAsync(
            0,
            tomadas.Count,
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParalelo, CancellationToken = ct },
            async (i, token) => resultados[i] = await EnviarAsync(tomadas[i], ahora, token));

        await using NpgsqlConnection conexion = await baseDatos.AbrirAsync(ct);
        for (int i = 0; i < tomadas.Count; i++)
        {
            await GuardarAsync(conexion, tomadas[i], resultados[i], ahora, ct);
        }

        var resultado = new ResultadoEnvio(
            resultados.Count(r => r.Estado == EstadoEntrega.Entregada),
            resultados.Count(r => r.Estado == EstadoEntrega.Pendiente),
            resultados.Count(r => r.Estado == EstadoEntrega.DeadLetter),
            resultados.Count(r => r.Estado == EstadoEntrega.Descartada));

        LogLote(logger, resultado.Entregadas, resultado.Fallidas, resultado.DeadLetter, resultado.Descartadas);
        return resultado;
    }

    // ── Tomar ─────────────────────────────────────────────────────────────────

    private async Task<List<Tomada>> TomarAsync(DateTimeOffset ahora, CancellationToken ct)
    {
        await using NpgsqlConnection conexion = await baseDatos.AbrirAsync(ct);
        await using NpgsqlCommand cmd = conexion.CreateCommand();
        cmd.CommandText = """
            WITH lote AS (
                SELECT e.id FROM integracion.entregas_webhook e
                JOIN integracion.suscripciones_webhook s ON s.id = e.suscripcion_id
                WHERE e.estado = 'PENDIENTE' AND e.proximo_intento_en <= @ahora AND s.estado = 'ACTIVA'
                ORDER BY e.proximo_intento_en
                LIMIT @lote
                FOR UPDATE OF e SKIP LOCKED)
            UPDATE integracion.entregas_webhook e SET proximo_intento_en = @arriendo
            FROM lote, integracion.suscripciones_webhook s
            WHERE e.id = lote.id AND s.id = e.suscripcion_id
            RETURNING e.id, e.suscripcion_id, e.evento_id, e.tipo, e.payload::text, e.intentos, e.primer_intento_en,
                      s.url, s.secreto_ct, s.secreto_key_id, s.secreto_anterior_ct, s.secreto_anterior_expira_en
            """;
        cmd.Parameters.AddWithValue("ahora", ahora);
        cmd.Parameters.AddWithValue("lote", _options.Lote);
        cmd.Parameters.AddWithValue("arriendo", ahora + Arriendo);

        var tomadas = new List<Tomada>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            DateTimeOffset? anteriorExpira = r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11);

            tomadas.Add(new Tomada(
                r.GetGuid(0),
                new SuscripcionWebhookId(r.GetGuid(1)),
                r.GetGuid(2),
                r.GetString(3),
                r.GetString(4),
                r.GetInt32(5),
                r.GetFieldValue<DateTimeOffset>(6),
                r.GetString(7),
                r.GetFieldValue<byte[]>(8),
                r.GetString(9),
                // The previous secret signs only while its transition lasts.
                anteriorExpira > ahora ? r.GetFieldValue<byte[]>(10) : null));
        }

        return tomadas;
    }

    // ── Enviar ────────────────────────────────────────────────────────────────

    private async Task<Desenlace> EnviarAsync(Tomada entrega, DateTimeOffset ahora, CancellationToken ct)
    {
        int intento = entrega.Intentos + 1;

        int? status = null;
        string? error;

        try
        {
            byte[] secreto = secretos.Desproteger(entrega.SuscripcionId, entrega.SecretoCt, entrega.SecretoKeyId);
            byte[]? anterior = entrega.SecretoAnteriorCt is { } anteriorCt
                ? secretos.Desproteger(entrega.SuscripcionId, anteriorCt, entrega.SecretoKeyId)
                : null;

            var cabeceras = new Dictionary<string, string>
            {
                [WebhookSigner.Header] = WebhookSigner.Firmar(ahora.ToUnixTimeSeconds(), entrega.Payload, secreto, anterior),
                [HeaderEventId] = entrega.EventoId.ToString(),
                [HeaderEventType] = entrega.Tipo,
                [HeaderDeliveryAttempt] = intento.ToString(CultureInfo.InvariantCulture)
            };

            using HttpResponseMessage respuesta = await http.PostAsync(new Uri(entrega.Url), entrega.Payload, cabeceras, ct);
            status = (int)respuesta.StatusCode;

            if (respuesta.IsSuccessStatusCode)
            {
                return new Desenlace(EstadoEntrega.Entregada, status, null, SuspenderSuscripcion: false);
            }

            if (respuesta.StatusCode == HttpStatusCode.Gone)
            {
                return new Desenlace(EstadoEntrega.Descartada, status, "410 Gone: el receptor pidió no recibir más.", SuspenderSuscripcion: true);
            }

            error = $"HTTP {status}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            error = $"Timeout de {_options.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s.";
        }
        catch (HttpRequestException ex)
        {
            error = (ex.InnerException as DestinoBloqueadoException)?.Message ?? ex.Message;
        }
#pragma warning disable CA1031 // A bad subscription (an unreadable secret, a broken URL) must not stop the batch.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
        }
#pragma warning restore CA1031

        EstadoEntrega estado = ahora - entrega.PrimerIntentoEn > EntregaWebhook.VentanaReintentos
            ? EstadoEntrega.DeadLetter
            : EstadoEntrega.Pendiente;

        return new Desenlace(estado, status, error, SuspenderSuscripcion: false);
    }

    // ── Guardar ───────────────────────────────────────────────────────────────

    private static async Task GuardarAsync(
        NpgsqlConnection conexion, Tomada entrega, Desenlace desenlace, DateTimeOffset ahora, CancellationToken ct)
    {
        await using NpgsqlTransaction transaccion = await conexion.BeginTransactionAsync(ct);

        await using (NpgsqlCommand cmd = conexion.CreateCommand())
        {
            cmd.Transaction = transaccion;
            cmd.CommandText = """
                UPDATE integracion.entregas_webhook
                SET estado = @estado, intentos = intentos + 1, proximo_intento_en = @proximo, ultimo_status = @status,
                    ultimo_error = @error, entregada_en = @entregada
                WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("id", entrega.Id);
            cmd.Parameters.AddWithValue("estado", CodigoDe(desenlace.Estado));
            cmd.Parameters.AddWithValue("proximo", desenlace.Estado == EstadoEntrega.Pendiente
                ? ahora + EntregaWebhook.Espera(entrega.Intentos + 1, Random.Shared.NextDouble())
                : ahora);
            cmd.Parameters.AddWithValue("status", (object?)desenlace.Status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("error", desenlace.Error is { } e ? Truncar(e) : DBNull.Value);
            cmd.Parameters.AddWithValue("entregada", desenlace.Estado == EstadoEntrega.Entregada ? ahora : DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (NpgsqlCommand cmd = conexion.CreateCommand())
        {
            cmd.Transaction = transaccion;

            // A failure counts, a success resets. Only an active subscription is suspended: a paused one stays paused.
            cmd.CommandText = desenlace.Estado == EstadoEntrega.Entregada
                ? "UPDATE integracion.suscripciones_webhook SET fallos_consecutivos = 0 WHERE id = @id"
                : """
                  UPDATE integracion.suscripciones_webhook
                  SET fallos_consecutivos = fallos_consecutivos + 1,
                      estado = CASE WHEN estado = 'ACTIVA' AND (@suspender OR fallos_consecutivos + 1 >= @limite)
                                    THEN 'SUSPENDIDA' ELSE estado END
                  WHERE id = @id
                  """;
            cmd.Parameters.AddWithValue("id", entrega.SuscripcionId.Value);
            cmd.Parameters.AddWithValue("suspender", desenlace.SuspenderSuscripcion);
            cmd.Parameters.AddWithValue("limite", EntregaWebhook.FallosParaSuspender);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await transaccion.CommitAsync(ct);
    }

    private static string CodigoDe(EstadoEntrega estado) => Domain.Dictamenes.CodigosDictamen.Codigo(estado);

    private static string Truncar(string error) =>
        error.Length <= EntregaWebhook.MaximoError ? error : error[..EntregaWebhook.MaximoError];

    private sealed record Tomada(
        Guid Id,
        SuscripcionWebhookId SuscripcionId,
        Guid EventoId,
        string Tipo,
        string Payload,
        int Intentos,
        DateTimeOffset PrimerIntentoEn,
        string Url,
        byte[] SecretoCt,
        string SecretoKeyId,
        byte[]? SecretoAnteriorCt);

    private sealed record Desenlace(EstadoEntrega Estado, int? Status, string? Error, bool SuspenderSuscripcion);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Webhooks: {Entregadas} entregadas, {Fallidas} a reintentar, {DeadLetter} a dead letter, {Descartadas} descartadas")]
    private static partial void LogLote(ILogger logger, int entregadas, int fallidas, int deadLetter, int descartadas);

    [LoggerMessage(Level = LogLevel.Error, Message = "El lote de webhooks falló")]
    private static partial void LogLoteFallido(ILogger logger, Exception exception);
}
