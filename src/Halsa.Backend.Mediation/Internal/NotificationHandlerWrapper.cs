using Microsoft.Extensions.DependencyInjection;

namespace Halsa.Backend.Mediation.Internal;

internal abstract class NotificationHandlerWrapper
{
    public abstract Task Handle(
        INotification notification,
        IServiceProvider serviceProvider,
        INotificationPublisher publisher,
        CancellationToken cancellationToken);
}

internal sealed class NotificationHandlerWrapperImpl<TNotification> : NotificationHandlerWrapper
    where TNotification : INotification
{
    public override Task Handle(
        INotification notification,
        IServiceProvider serviceProvider,
        INotificationPublisher publisher,
        CancellationToken cancellationToken)
    {
        NotificationHandlerExecutor[] executors = serviceProvider
            .GetServices<INotificationHandler<TNotification>>()
            .Select(handler => new NotificationHandlerExecutor(
                handler,
                (theNotification, token) => handler.Handle((TNotification)theNotification, token)))
            .ToArray();

        return publisher.Publish(executors, notification, cancellationToken);
    }
}
