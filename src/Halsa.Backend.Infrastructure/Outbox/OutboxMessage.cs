using System.Text.Json;
using Halsa.Backend.Infrastructure.EventSourcing;
using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Infrastructure.Outbox;

/// <summary>
/// An integration event waiting to be published. Written in the same transaction as the state
/// change it describes, which is what makes "at least once" delivery possible without 2PC.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    /// <summary>Stable contract name of the integration event.</summary>
    public string Type { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTime OccurredOnUtc { get; set; }

    public DateTime? ProcessedOnUtc { get; set; }

    public int Attempts { get; set; }

    public string? Error { get; set; }

    /// <summary>The row for <paramref name="integrationEvent"/>, keyed by its id and typed by its CLR name.</summary>
    public static OutboxMessage Desde(IIntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        return new OutboxMessage
        {
            Id = integrationEvent.EventId,
            Type = integrationEvent.GetType().FullName!,
            Content = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), EventSerializer.Options),
            OccurredOnUtc = integrationEvent.OccurredOnUtc
        };
    }
}
