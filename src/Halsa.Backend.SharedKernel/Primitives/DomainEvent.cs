namespace Halsa.Backend.SharedKernel.Primitives;

/// <summary>
/// Convenience base record for domain events. Derived events stay immutable records so that
/// replaying a stream can never mutate history.
/// </summary>
public abstract record DomainEvent : IDomainEvent
{
    protected DomainEvent()
    {
        EventId = Guid.CreateVersion7();
        OccurredOnUtc = DateTime.UtcNow;
    }

    public Guid EventId { get; init; }

    public DateTime OccurredOnUtc { get; init; }
}
