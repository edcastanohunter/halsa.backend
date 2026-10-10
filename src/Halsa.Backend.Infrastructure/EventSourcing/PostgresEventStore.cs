using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.EventSourcing;
using Halsa.Backend.SharedKernel.Primitives;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Halsa.Backend.Infrastructure.EventSourcing;

/// <summary>
/// PostgreSQL-backed event store. Concurrency is enforced by the unique index on
/// (stream_id, version) rather than by a read-then-check race.
/// </summary>
internal sealed class PostgresEventStore(
    ApplicationDbContext context,
    EventSerializer serializer,
    IClock clock)
    : IEventStore
{
    private const string UniqueViolation = "23505";

    public async Task<IReadOnlyList<IDomainEvent>> ReadStreamAsync(
        string streamId,
        int fromVersion = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);

        List<StoredEvent> stored = await context.Events
            .AsNoTracking()
            .Where(e => e.StreamId == streamId && e.Version >= fromVersion)
            .OrderBy(e => e.Version)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. stored.Select(e => serializer.Deserialize(e.EventType, e.Payload))];
    }

    public async Task AppendToStreamAsync(
        string streamId,
        string streamType,
        int expectedVersion,
        IReadOnlyList<IDomainEvent> events,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return;
        }

        string? serialisedMetadata = metadata is { Count: > 0 }
            ? System.Text.Json.JsonSerializer.Serialize(metadata, EventSerializer.Options)
            : null;

        DateTime recordedOnUtc = clock.UtcNow;
        int version = expectedVersion;

        foreach (IDomainEvent domainEvent in events)
        {
            version++;

            context.Events.Add(new StoredEvent
            {
                EventId = domainEvent.EventId,
                StreamId = streamId,
                StreamType = streamType,
                Version = version,
                EventType = serializer.GetName(domainEvent),
                Payload = serializer.Serialize(domainEvent),
                Metadata = serialisedMetadata,
                OccurredOnUtc = domainEvent.OccurredOnUtc,
                RecordedOnUtc = recordedOnUtc
            });
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            int actualVersion = await GetCurrentVersionAsync(streamId, cancellationToken).ConfigureAwait(false);

            throw new ConcurrencyException(streamId, expectedVersion, actualVersion);
        }
    }

    public Task<bool> StreamExistsAsync(string streamId, CancellationToken cancellationToken = default) =>
        context.Events.AsNoTracking().AnyAsync(e => e.StreamId == streamId, cancellationToken);

    private async Task<int> GetCurrentVersionAsync(string streamId, CancellationToken cancellationToken)
    {
        int? max = await context.Events
            .AsNoTracking()
            .Where(e => e.StreamId == streamId)
            .MaxAsync(e => (int?)e.Version, cancellationToken)
            .ConfigureAwait(false);

        return max ?? EventStream.NoStream;
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: UniqueViolation };
}
