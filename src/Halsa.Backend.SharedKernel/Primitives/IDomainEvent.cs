namespace Halsa.Backend.SharedKernel.Primitives;

/// <summary>
/// Something that happened inside the domain, expressed in the ubiquitous language and in past tense.
/// Domain events are the write model's source of truth for event-sourced aggregates.
/// </summary>
public interface IDomainEvent
{
    Guid EventId { get; }

    DateTime OccurredOnUtc { get; }
}
