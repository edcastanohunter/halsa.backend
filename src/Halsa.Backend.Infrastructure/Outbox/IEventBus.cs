namespace Halsa.Backend.Infrastructure.Outbox;

/// <summary>
/// Transport for integration events. Swap the implementation for Kafka, Azure Service Bus or
/// RabbitMQ without touching a single handler: the outbox already guarantees at-least-once hand-off.
/// </summary>
public interface IEventBus
{
    Task PublishAsync(string eventType, string payload, CancellationToken cancellationToken = default);
}
