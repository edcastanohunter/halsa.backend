using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Application.Abstractions.Events;

/// <summary>
/// Adapts a domain event (which knows nothing about messaging) into a mediator notification,
/// keeping the shared kernel free of any dependency on the mediation library.
/// </summary>
public sealed record DomainEventNotification<TDomainEvent>(TDomainEvent DomainEvent) : INotification
    where TDomainEvent : IDomainEvent;

/// <summary>
/// Implement this to react to a domain event. Handlers run in-process, inside the same request,
/// after the aggregate's events have been persisted.
/// </summary>
public interface IDomainEventHandler<in TDomainEvent> : INotificationHandler<DomainEventNotification<TDomainEvent>>
    where TDomainEvent : IDomainEvent;
