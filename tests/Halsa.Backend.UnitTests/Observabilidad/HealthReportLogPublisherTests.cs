using Halsa.Backend.Infrastructure.Observabilidad;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Halsa.Backend.UnitTests.Observabilidad;

/// <summary>SPEC 13, paso 9: el estado de salud llega al log con <c>Alerta = HealthCheck</c> solo cuando no está sano.</summary>
public sealed class HealthReportLogPublisherTests
{
    [Theory]
    [InlineData(HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unhealthy)]
    public async Task NotHealthy_ShouldLogAWarningWithTheAlertAndTheComponent(HealthStatus estado)
    {
        var logger = new LoggerDePrueba();

        await new HealthReportLogPublisher(logger).PublishAsync(Reporte(("antivirus", estado), ("postgres", HealthStatus.Healthy)), default);

        (LogLevel nivel, string mensaje) = logger.Entradas.ShouldHaveSingleItem();
        nivel.ShouldBe(LogLevel.Warning);
        mensaje.ShouldContain(Alertas.HealthCheck);
        mensaje.ShouldContain($"antivirus={estado}");
        mensaje.ShouldNotContain("postgres");
    }

    [Fact]
    public async Task Healthy_ShouldNotWarn()
    {
        var logger = new LoggerDePrueba();

        await new HealthReportLogPublisher(logger).PublishAsync(Reporte(("postgres", HealthStatus.Healthy)), default);

        logger.Entradas.ShouldAllBe(e => e.Nivel < LogLevel.Warning);
    }

    private static HealthReport Reporte(params (string Nombre, HealthStatus Estado)[] componentes) => new(
        componentes.ToDictionary(
            c => c.Nombre,
            c => new HealthReportEntry(c.Estado, null, TimeSpan.Zero, null, null)),
        TimeSpan.Zero);

    private sealed class LoggerDePrueba : ILogger<HealthReportLogPublisher>
    {
        public List<(LogLevel Nivel, string Mensaje)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entradas.Add((logLevel, formatter(state, exception)));
    }
}
