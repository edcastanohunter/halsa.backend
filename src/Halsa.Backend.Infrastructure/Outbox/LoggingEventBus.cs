using Microsoft.Extensions.Logging;

namespace Halsa.Backend.Infrastructure.Outbox;

/// <summary>
/// Default transport: writes the event to the log. Enough to develop against, and a deliberate
/// reminder to plug a real broker in before going to production.
/// </summary>
internal sealed partial class LoggingEventBus(ILogger<LoggingEventBus> logger) : IEventBus
{
    public Task PublishAsync(string eventType, string payload, CancellationToken cancellationToken = default)
    {
        Published(logger, eventType, payload);

        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information,
        Message = "Integration event {EventType} published: {Payload}")]
    private static partial void Published(ILogger logger, string eventType, string payload);
}
