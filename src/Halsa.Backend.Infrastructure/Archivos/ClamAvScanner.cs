using System.Net.Sockets;
using Halsa.Backend.Application.Abstractions.Archivos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.Infrastructure.Archivos;

/// <summary>
/// <see cref="IAntivirusScanner"/> sobre <c>clamd</c> (<c>INSTREAM</c>). Falla cerrado: una conexión rechazada, un
/// error de <c>clamd</c> o un análisis de más de <see cref="AntivirusOptions.Timeout"/> devuelven
/// <see cref="ResultadoAntivirus.NoDisponible"/>, nunca <see cref="ResultadoAntivirus.Limpio"/>.
/// </summary>
internal sealed partial class ClamAvScanner(IOptions<AntivirusOptions> options, ILogger<ClamAvScanner> logger)
    : IAntivirusScanner
{
    private const string Prefijo = "stream:";
    private const string SufijoInfectado = "FOUND";

    private readonly AntivirusOptions _options = options.Value;

    public async Task<ResultadoAntivirus> AnalizarAsync(Stream contenido, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contenido);

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(_options.Timeout);

        try
        {
            string respuesta = await new ClamdClient(_options.Host, _options.Puerto).InstreamAsync(contenido, limite.Token);
            ResultadoAntivirus resultado = Interpretar(respuesta);

            if (resultado is ResultadoAntivirus.NoDisponibleResultado)
            {
                LogRespuestaInesperada(logger, respuesta);
            }

            return resultado;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogNoDisponible(logger, null, "timeout");
            return ResultadoAntivirus.NoDisponible("timeout");
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            LogNoDisponible(logger, exception, exception.GetType().Name);
            return ResultadoAntivirus.NoDisponible(exception.GetType().Name);
        }
    }

    /// <summary><c>"stream: OK"</c>, <c>"stream: {firma} FOUND"</c>; cualquier otra cosa es un error de <c>clamd</c>.</summary>
    internal static ResultadoAntivirus Interpretar(string respuesta)
    {
        ArgumentNullException.ThrowIfNull(respuesta);

        if (!respuesta.StartsWith(Prefijo, StringComparison.Ordinal))
        {
            return ResultadoAntivirus.NoDisponible(respuesta);
        }

        string veredicto = respuesta[Prefijo.Length..].Trim();

        if (veredicto == "OK")
        {
            return ResultadoAntivirus.Limpio;
        }

        if (veredicto.EndsWith($" {SufijoInfectado}", StringComparison.Ordinal))
        {
            return ResultadoAntivirus.Infectado(veredicto[..^(SufijoInfectado.Length + 1)].Trim());
        }

        return ResultadoAntivirus.NoDisponible(respuesta);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "El antivirus no está disponible ({Motivo}): el archivo se rechaza")]
    private static partial void LogNoDisponible(ILogger logger, Exception? exception, string motivo);

    [LoggerMessage(Level = LogLevel.Error, Message = "Respuesta inesperada de clamd: {Respuesta}")]
    private static partial void LogRespuestaInesperada(ILogger logger, string respuesta);
}

/// <summary>
/// <c>antivirus</c> en <c>/health</c>: <c>zPING</c> a <c>clamd</c>. Sin antivirus la API sigue respondiendo, pero no
/// acepta soportes, así que se registra como <see cref="HealthStatus.Degraded"/>.
/// </summary>
internal sealed class AntivirusHealthCheck(IOptions<AntivirusOptions> options) : IHealthCheck
{
    public const string Nombre = "antivirus";

    private static readonly TimeSpan TimeoutPing = TimeSpan.FromSeconds(5);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(TimeoutPing);

        try
        {
            string respuesta = await new ClamdClient(options.Value.Host, options.Value.Puerto).PingAsync(limite.Token);

            return respuesta == "PONG"
                ? HealthCheckResult.Healthy()
                : new HealthCheckResult(context.Registration.FailureStatus, $"clamd respondió '{respuesta}'.");
        }
        catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "clamd no responde.", exception);
        }
    }
}
