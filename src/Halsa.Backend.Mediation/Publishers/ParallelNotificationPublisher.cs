namespace Halsa.Backend.Mediation.Publishers;

/// <summary>
/// Runs every handler concurrently and surfaces all failures as an <see cref="AggregateException"/>.
/// Only use it when handlers are provably independent and hold no shared, non-thread-safe state.
/// </summary>
public sealed class ParallelNotificationPublisher : INotificationPublisher
{
    public Task Publish(
        IReadOnlyList<NotificationHandlerExecutor> handlerExecutors,
        INotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handlerExecutors);

        Task[] tasks = [.. handlerExecutors.Select(e => e.HandlerCallback(notification, cancellationToken))];

        return Task.WhenAll(tasks);
    }
}
