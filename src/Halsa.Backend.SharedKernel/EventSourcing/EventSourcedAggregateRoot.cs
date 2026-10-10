using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.SharedKernel.EventSourcing;

/// <summary>
/// An aggregate whose state is derived exclusively from its event stream.
/// <para>
/// Behaviour methods validate invariants and then call <see cref="RaiseAndApply"/>; the
/// <see cref="Apply"/> switch is the only place that mutates state, and it must never fail,
/// because it also runs while replaying history.
/// </para>
/// </summary>
public abstract class EventSourcedAggregateRoot<TId> : AggregateRoot<TId>
    where TId : notnull
{
    /// <summary>Version of an aggregate whose stream does not exist yet.</summary>
    public const int NoStream = EventStream.NoStream;

    protected EventSourcedAggregateRoot(TId id) : base(id)
    {
    }

    protected EventSourcedAggregateRoot()
    {
    }

    /// <summary>Version after applying every event currently known to this instance.</summary>
    public int Version { get; private set; } = NoStream;

    /// <summary>
    /// Version the aggregate had when it was loaded. Used as the expected version for
    /// optimistic concurrency when appending to the store.
    /// </summary>
    public int OriginalVersion => Version - DomainEvents.Count;

    /// <summary>Events raised since the aggregate was loaded, in order.</summary>
    public IReadOnlyList<IDomainEvent> UncommittedEvents => [.. DomainEvents];

    /// <summary>Rebuilds the aggregate from its persisted stream.</summary>
    public void LoadFromHistory(IEnumerable<IDomainEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        foreach (IDomainEvent domainEvent in history)
        {
            Apply(domainEvent);
            Version++;
        }

        ClearDomainEvents();
    }

    /// <summary>Applies a new event to the aggregate and queues it for persistence.</summary>
    protected void RaiseAndApply(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        Apply(domainEvent);
        Version++;
        Raise(domainEvent);
    }

    /// <summary>
    /// Pure state transition. Must be total and side-effect free: it runs on replay as well as on write.
    /// </summary>
    protected abstract void Apply(IDomainEvent domainEvent);
}
