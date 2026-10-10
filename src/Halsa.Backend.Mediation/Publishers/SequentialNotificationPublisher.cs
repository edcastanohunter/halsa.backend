namespace Halsa.Backend.Mediation.Publishers;

/// <summary>
/// Runs handlers one after another and stops at the first exception. The safe default: handlers
/// usually share a DbContext, and EF Core is not thread safe.
/// </summary>
public sealed class SequentialNotificationPublisher : INotificationPublisher
{
    public async Task Publish(
        IReadOnlyList<NotificationHandlerExecutor> handlerExecutors,
        INotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handlerExecutors);

        foreach (NotificationHandlerExecutor executor in handlerExecutors)
        {
            await executor.HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
        }
    }
}
