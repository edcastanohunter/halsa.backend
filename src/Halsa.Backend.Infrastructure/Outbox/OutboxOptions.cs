namespace Halsa.Backend.Infrastructure.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>How often the dispatcher polls for unprocessed messages.</summary>
    public int IntervalInSeconds { get; set; } = 5;

    /// <summary>Rows claimed per pass. Keep it small enough that one pass fits comfortably in a transaction.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Attempts before a message is left alone for a human to look at.</summary>
    public int MaxAttempts { get; set; } = 5;
}
