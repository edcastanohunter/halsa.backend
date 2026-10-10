namespace Halsa.Backend.Mediation;

/// <summary>Fans a notification out to every registered handler.</summary>
public interface IPublisher
{
    Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification;

    Task Publish(INotification notification, CancellationToken cancellationToken = default);
}
