using System.Text.Json;
using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.EventSourcing;
using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Infrastructure.Outbox;

/// <summary>
/// Writes the integration event into the outbox table using the caller's DbContext, so it is
/// committed by the same transaction that committed the state change. It is never sent from here.
/// </summary>
internal sealed class OutboxIntegrationEventPublisher(ApplicationDbContext context) : IIntegrationEventPublisher
{
    public Task EnqueueAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        context.OutboxMessages.Add(new OutboxMessage
        {
            Id = integrationEvent.EventId,
            Type = integrationEvent.GetType().FullName!,
            Content = JsonSerializer.Serialize(
                integrationEvent,
                integrationEvent.GetType(),
                EventSerializer.Options),
            OccurredOnUtc = integrationEvent.OccurredOnUtc
        });

        return Task.CompletedTask;
    }
}
