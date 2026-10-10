using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.SharedKernel.EventSourcing;
using Halsa.Backend.SharedKernel.Primitives;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Halsa.Backend.Infrastructure.EventSourcing;

/// <summary>
/// Loads an aggregate by replaying its stream and saves it by appending the events it raised.
/// <para>
/// Append, projection and outbox writes share one transaction, so the read model can never drift
/// from the log and an integration event can never be published for a write that rolled back.
/// The cost is that a slow projection slows the command; move a projection to a catch-up
/// subscription when that becomes the bottleneck.
/// </para>
/// </summary>
internal sealed class EventSourcedRepository<TAggregate, TId>(
    ApplicationDbContext context,
    IEventStore eventStore,
    IDomainEventDispatcher domainEventDispatcher)
    : IEventSourcedRepository<TAggregate, TId>
    where TAggregate : EventSourcedAggregateRoot<TId>
    where TId : notnull
{
    private static readonly string StreamType = typeof(TAggregate).Name;

    public async Task<TAggregate?> LoadAsync(TId id, CancellationToken cancellationToken = default)
    {
        string streamId = EventStream.For(StreamType, id);

        IReadOnlyList<IDomainEvent> history = await eventStore
            .ReadStreamAsync(streamId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (history.Count == 0)
        {
            return null;
        }

        var aggregate = CreateEmpty();
        aggregate.LoadFromHistory(history);

        return aggregate;
    }

    public async Task SaveAsync(TAggregate aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        IReadOnlyList<IDomainEvent> uncommitted = aggregate.UncommittedEvents;

        if (uncommitted.Count == 0)
        {
            return;
        }

        string streamId = EventStream.For(StreamType, aggregate.Id);

        // When the caller already owns a transaction it also owns the execution strategy; opening
        // our own means the whole append-project-commit cycle has to be the retriable unit, or EF
        // refuses the user-initiated transaction while retries are enabled on the connection.
        if (context.Database.CurrentTransaction is not null)
        {
            await SaveCoreAsync(aggregate, streamId, uncommitted, ownsTransaction: false, cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        IExecutionStrategy strategy = context.Database.CreateExecutionStrategy();

        await strategy
            .ExecuteAsync(
                token => SaveCoreAsync(aggregate, streamId, uncommitted, ownsTransaction: true, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SaveCoreAsync(
        TAggregate aggregate,
        string streamId,
        IReadOnlyList<IDomainEvent> uncommitted,
        bool ownsTransaction,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = ownsTransaction
            ? await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            await eventStore.AppendToStreamAsync(
                streamId,
                StreamType,
                aggregate.OriginalVersion,
                uncommitted,
                metadata: null,
                cancellationToken).ConfigureAwait(false);

            // Projections and outbox writes are tracked by the same context...
            await domainEventDispatcher.DispatchAsync(uncommitted, cancellationToken).ConfigureAwait(false);

            // ...and flushed inside the same transaction.
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            aggregate.ClearDomainEvents();
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Event-sourced aggregates expose a private parameterless constructor precisely so that only
    /// the stream can decide their state.
    /// </summary>
    private static TAggregate CreateEmpty() =>
        (TAggregate)(Activator.CreateInstance(typeof(TAggregate), nonPublic: true)
                     ?? throw new InvalidOperationException(
                         $"'{typeof(TAggregate).FullName}' needs a private parameterless constructor for replay."));
}
