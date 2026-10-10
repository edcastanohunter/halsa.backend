using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Application.Abstractions.Events;

/// <summary>
/// Enqueues an integration event for publication. The implementation writes it to the outbox in the
/// same transaction as the state change, so "state changed" and "event published" can never diverge.
/// A background dispatcher then ships it to the broker (Kafka, Service Bus, ...) at least once.
/// </summary>
public interface IIntegrationEventPublisher
{
    Task EnqueueAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default);
}
