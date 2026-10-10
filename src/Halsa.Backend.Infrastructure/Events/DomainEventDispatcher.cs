using System.Collections.Concurrent;
using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Infrastructure.Events;

/// <summary>
/// Wraps each domain event in <see cref="DomainEventNotification{TDomainEvent}"/> and publishes it
/// through the mediator, so reactions stay ordinary, testable handlers.
/// </summary>
internal sealed class DomainEventDispatcher(IPublisher publisher) : IDomainEventDispatcher
{
    private static readonly ConcurrentDictionary<Type, Func<IDomainEvent, INotification>> Factories = new();

    public async Task DispatchAsync(
        IEnumerable<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        foreach (IDomainEvent domainEvent in domainEvents)
        {
            INotification notification = Factories
                .GetOrAdd(domainEvent.GetType(), CreateFactory)
                .Invoke(domainEvent);

            await publisher.Publish(notification, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Func<IDomainEvent, INotification> CreateFactory(Type domainEventType)
    {
        Type notificationType = typeof(DomainEventNotification<>).MakeGenericType(domainEventType);

        return domainEvent => (INotification)Activator.CreateInstance(notificationType, domainEvent)!;
    }
}
