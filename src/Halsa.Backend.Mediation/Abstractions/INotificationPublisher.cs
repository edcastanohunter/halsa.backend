namespace Halsa.Backend.Mediation;

/// <summary>A resolved notification handler, ready to invoke.</summary>
public readonly record struct NotificationHandlerExecutor(
    object HandlerInstance,
    Func<INotification, CancellationToken, Task> HandlerCallback);

/// <summary>
/// Strategy for fanning a notification out. Swap it to change ordering, error handling or
/// parallelism without touching the mediator.
/// </summary>
public interface INotificationPublisher
{
    Task Publish(
        IReadOnlyList<NotificationHandlerExecutor> handlerExecutors,
        INotification notification,
        CancellationToken cancellationToken);
}
