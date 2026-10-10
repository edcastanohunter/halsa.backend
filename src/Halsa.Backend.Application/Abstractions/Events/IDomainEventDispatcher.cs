using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Application.Abstractions.Events;

/// <summary>Publishes domain events raised by aggregates during the current unit of work.</summary>
public interface IDomainEventDispatcher
{
    Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default);
}
