namespace Halsa.Backend.SharedKernel.EventSourcing;

/// <summary>
/// Naming convention for streams: <c>&lt;aggregate-type&gt;-&lt;aggregate-id&gt;</c>.
/// Keeping it in one place stops each module from inventing its own layout.
/// </summary>
public static class EventStream
{
    /// <summary>Version of a stream that does not exist yet.</summary>
    public const int NoStream = -1;

    public static string For(string aggregateType, object aggregateId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateType, nameof(aggregateType));
        ArgumentNullException.ThrowIfNull(aggregateId);

        return $"{aggregateType}-{aggregateId}";
    }
}
