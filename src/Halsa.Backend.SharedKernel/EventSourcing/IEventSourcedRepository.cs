namespace Halsa.Backend.SharedKernel.EventSourcing;

/// <summary>
/// Repository over an event stream. Loading replays history; saving appends the uncommitted events
/// with optimistic concurrency.
/// </summary>
public interface IEventSourcedRepository<TAggregate, in TId>
    where TAggregate : EventSourcedAggregateRoot<TId>
    where TId : notnull
{
    Task<TAggregate?> LoadAsync(TId id, CancellationToken cancellationToken = default);

    Task SaveAsync(TAggregate aggregate, CancellationToken cancellationToken = default);
}
