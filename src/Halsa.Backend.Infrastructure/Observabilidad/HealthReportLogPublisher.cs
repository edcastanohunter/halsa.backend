using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Halsa.Backend.Infrastructure.Observabilidad;

/// <summary>
/// SPEC 13: publica el estado de los health checks en el log cada minuto, para que Seq alerte si un componente sigue en
/// <c>Degraded</c> o <c>Unhealthy</c> más de 5 minutos (runbook 07: cinco eventos <see cref="Alertas.HealthCheck"/> en
/// 5 minutos). <c>/health</c> solo responde a quien lo consulta; esto deja rastro aunque nadie pregunte.
/// </summary>
internal sealed partial class HealthReportLogPublisher(ILogger<HealthReportLogPublisher> logger) : IHealthCheckPublisher
{
    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.Status == HealthStatus.Healthy)
        {
            LogSano(logger);
            return Task.CompletedTask;
        }

        string componentes = string.Join(", ", report.Entries
            .Where(e => e.Value.Status != HealthStatus.Healthy)
            .Select(e => $"{e.Key}={e.Value.Status}"));

        LogNoSano(logger, Alertas.HealthCheck, report.Status, componentes);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "HealthCheck Healthy")]
    private static partial void LogSano(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alerta {Alerta}: el estado es {Estado} ({Componentes})")]
    private static partial void LogNoSano(ILogger logger, string alerta, HealthStatus estado, string componentes);
}
