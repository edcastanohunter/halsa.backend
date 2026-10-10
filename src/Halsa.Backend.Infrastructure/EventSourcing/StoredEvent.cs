namespace Halsa.Backend.Infrastructure.EventSourcing;

/// <summary>
/// One row of the append-only log. Nothing ever updates or deletes a row in this table.
/// </summary>
public sealed class StoredEvent
{
    /// <summary>Global, gap-free-enough ordering across every stream. Drives catch-up subscriptions.</summary>
    public long Sequence { get; set; }

    public Guid EventId { get; set; }

    /// <summary>Stream this event belongs to, e.g. <c>work-item-0192...</c>.</summary>
    public string StreamId { get; set; } = string.Empty;

    /// <summary>Aggregate type that owns the stream. Handy for per-type replays.</summary>
    public string StreamType { get; set; } = string.Empty;

    /// <summary>Zero-based position inside the stream. Unique together with <see cref="StreamId"/>.</summary>
    public int Version { get; set; }

    /// <summary>Stable event name from <c>EventNameAttribute</c>, never a CLR type name.</summary>
    public string EventType { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public string? Metadata { get; set; }

    public DateTime OccurredOnUtc { get; set; }

    public DateTime RecordedOnUtc { get; set; }
}
