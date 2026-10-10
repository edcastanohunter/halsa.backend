using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.SharedKernel.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.Infrastructure.Outbox;

/// <summary>
/// Drains the outbox. Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c>, so several instances of
/// the API can run this concurrently without processing the same message twice.
/// </summary>
internal sealed partial class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    ILogger<OutboxProcessor> logger)
    : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.IntervalInSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessBatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // The dispatcher must survive any single bad batch.
            catch (Exception exception)
            {
                BatchFailed(logger, exception);
            }
#pragma warning restore CA1031
        }
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        // Retries are enabled on the connection, so EF refuses a transaction that is not itself the
        // retriable unit: the whole claim-publish-commit cycle runs inside the execution strategy.
        IExecutionStrategy strategy = context.Database.CreateExecutionStrategy();

        await strategy
            .ExecuteAsync(token => ProcessClaimedBatchAsync(context, eventBus, clock, token), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ProcessClaimedBatchAsync(
        ApplicationDbContext context,
        IEventBus eventBus,
        IClock clock,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        List<OutboxMessage> messages = await context.OutboxMessages
            .FromSql($"""
                      SELECT *
                      FROM outbox.outbox_messages
                      WHERE processed_on_utc IS NULL AND attempts < {_options.MaxAttempts}
                      ORDER BY occurred_on_utc
                      LIMIT {_options.BatchSize}
                      FOR UPDATE SKIP LOCKED
                      """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (messages.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (OutboxMessage message in messages)
        {
            message.Attempts++;

            try
            {
                await eventBus
                    .PublishAsync(message.Type, message.Content, cancellationToken)
                    .ConfigureAwait(false);

                message.ProcessedOnUtc = clock.UtcNow;
                message.Error = null;
            }
#pragma warning disable CA1031 // One poisoned message must not stop the rest of the batch.
            catch (Exception exception)
            {
                message.Error = exception.ToString();
                MessageFailed(logger, message.Id, message.Type, exception);
            }
#pragma warning restore CA1031
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 3010, Level = LogLevel.Error, Message = "Outbox batch failed")]
    private static partial void BatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3011, Level = LogLevel.Error,
        Message = "Outbox message {MessageId} of type {MessageType} failed")]
    private static partial void MessageFailed(ILogger logger, Guid messageId, string messageType, Exception exception);
}
