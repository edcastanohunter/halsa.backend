using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Application.IntegrationEvents;
using Halsa.Backend.Domain.WorkItems;
using Microsoft.Extensions.Logging;

namespace Halsa.Backend.Application.WorkItems.EventHandlers;

/// <summary>
/// Translates an internal domain event into the module's public integration event and hands it to
/// the outbox. Nothing outside this module ever sees <see cref="WorkItemCompleted"/> itself.
/// </summary>
internal sealed partial class WorkItemCompletedDomainEventHandler(
    IIntegrationEventPublisher integrationEventPublisher,
    ILogger<WorkItemCompletedDomainEventHandler> logger)
    : IDomainEventHandler<WorkItemCompleted>
{
    public async Task Handle(
        DomainEventNotification<WorkItemCompleted> notification,
        CancellationToken cancellationToken)
    {
        WorkItemCompleted domainEvent = notification.DomainEvent;

        Completed(logger, domainEvent.WorkItemId);

        await integrationEventPublisher.EnqueueAsync(
            new WorkItemCompletedIntegrationEvent(domainEvent.WorkItemId, domainEvent.CompletedOnUtc),
            cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Work item {WorkItemId} completed")]
    private static partial void Completed(ILogger logger, Guid workItemId);
}
