namespace Halsa.Backend.Mediation;

/// <summary>One of possibly many reactions to a notification.</summary>
public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    Task Handle(TNotification notification, CancellationToken cancellationToken);
}
