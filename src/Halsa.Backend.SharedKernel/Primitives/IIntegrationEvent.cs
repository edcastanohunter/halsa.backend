namespace Halsa.Backend.SharedKernel.Primitives;

/// <summary>
/// A fact published outside its owning module's boundary. Integration events are part of a
/// module's public contract: version them, and never leak internal domain types through them.
/// </summary>
public interface IIntegrationEvent
{
    Guid EventId { get; }

    DateTime OccurredOnUtc { get; }
}

/// <inheritdoc cref="IIntegrationEvent" />
public abstract record IntegrationEvent : IIntegrationEvent
{
    protected IntegrationEvent()
    {
        EventId = Guid.CreateVersion7();
        OccurredOnUtc = DateTime.UtcNow;
    }

    public Guid EventId { get; init; }

    public DateTime OccurredOnUtc { get; init; }
}
