using Halsa.Backend.Infrastructure.Observabilidad;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace Halsa.Backend.Infrastructure.Worker.Jobs;

/// <summary>
/// SPEC 13: cada 5 minutos, un <c>GET</c> a <see cref="ObservabilidadOptions.HeartbeatUrl"/>, un servicio externo de
/// tipo <em>dead man's switch</em>. Si el Worker (o el VPS entero) cae, los pings dejan de llegar y es el servicio
/// externo el que alerta. Un ping fallido solo se registra: el aviso es justamente que falte.
/// </summary>
[DisallowConcurrentExecution]
internal sealed partial class HeartbeatJob(
    HeartbeatHttpClient http,
    IOptions<ObservabilidadOptions> options,
    ILogger<HeartbeatJob> logger) : IJob
{
    public static readonly JobKey Key = new("heartbeat", TrabajosWorker.Grupo);

    public Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return LatirAsync(context.CancellationToken);
    }

    /// <summary>Devuelve si el servicio externo recibió el ping.</summary>
    internal async Task<bool> LatirAsync(CancellationToken ct)
    {
        string? url = options.Value.HeartbeatUrl;

        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        try
        {
            using HttpResponseMessage response = await http.Cliente.GetAsync(new Uri(url), ct);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            LogFallo(logger, $"HTTP {(int)response.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            LogFallo(logger, ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            LogFallo(logger, "timeout");
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Heartbeat: el monitor externo no recibió el ping ({Motivo})")]
    private static partial void LogFallo(ILogger logger, string motivo);
}

/// <summary>El cliente del heartbeat: corto, sin cookies ni redirecciones. Singleton, como el de los webhooks.</summary>
internal sealed class HeartbeatHttpClient : IDisposable
{
    public HeartbeatHttpClient()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
    }

    /// <summary>Para los tests: otro handler.</summary>
    internal HeartbeatHttpClient(HttpMessageHandler handler) =>
        Cliente = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

    public HttpClient Cliente { get; }

    public void Dispose() => Cliente.Dispose();
}
