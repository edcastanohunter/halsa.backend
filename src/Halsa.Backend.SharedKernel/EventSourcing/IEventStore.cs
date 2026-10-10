using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.SharedKernel.EventSourcing;

/// <summary>
/// Append-only log of domain events. The only write-model persistence an event-sourced
/// aggregate is allowed to know about.
/// </summary>
public interface IEventStore
{
    /// <summary>Reads a stream in order. Returns an empty list when the stream does not exist.</summary>
    Task<IReadOnlyList<IDomainEvent>> ReadStreamAsync(
        string streamId,
        int fromVersion = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends events, failing with <see cref="ConcurrencyException"/> when the stream moved on
    /// since it was read. <paramref name="expectedVersion"/> is
    /// <see cref="EventSourcedAggregateRoot{TId}.NoStream"/> for a brand new stream.
    /// </summary>
    Task AppendToStreamAsync(
        string streamId,
        string streamType,
        int expectedVersion,
        IReadOnlyList<IDomainEvent> events,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>True when at least one event exists for the stream.</summary>
    Task<bool> StreamExistsAsync(string streamId, CancellationToken cancellationToken = default);
}
