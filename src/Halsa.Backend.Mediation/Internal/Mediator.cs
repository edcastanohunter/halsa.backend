using System.Collections.Concurrent;

namespace Halsa.Backend.Mediation.Internal;

/// <summary>
/// In-process mediator. Deliberately small: it resolves a handler, wraps it in the registered
/// pipeline behaviours and invokes it. No reflection after the first call for a given message type.
/// </summary>
internal sealed class Mediator(IServiceProvider serviceProvider, INotificationPublisher notificationPublisher)
    : IMediator
{
    private static readonly ConcurrentDictionary<Type, RequestHandlerWrapperBase> RequestWrappers = new();
    private static readonly ConcurrentDictionary<Type, NotificationHandlerWrapper> NotificationWrappers = new();

    public Task<TResponse> Send<TResponse>(
        IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = (RequestHandlerWrapper<TResponse>)RequestWrappers.GetOrAdd(
            request.GetType(),
            static (requestType, responseType) => CreateRequestWrapper(requestType, responseType),
            typeof(TResponse));

        return wrapper.Handle(request, serviceProvider, cancellationToken);
    }

    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        RequestHandlerWrapperBase wrapper = RequestWrappers.GetOrAdd(
            request.GetType(),
            static requestType =>
            {
                Type responseType = GetResponseType(requestType);
                return CreateRequestWrapper(requestType, responseType);
            });

        return wrapper.HandleUntyped(request, serviceProvider, cancellationToken);
    }

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);

        return PublishCore(notification, cancellationToken);
    }

    public Task Publish(INotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return PublishCore(notification, cancellationToken);
    }

    private Task PublishCore(INotification notification, CancellationToken cancellationToken)
    {
        NotificationHandlerWrapper wrapper = NotificationWrappers.GetOrAdd(
            notification.GetType(),
            static notificationType => (NotificationHandlerWrapper)Activator.CreateInstance(
                typeof(NotificationHandlerWrapperImpl<>).MakeGenericType(notificationType))!);

        return wrapper.Handle(notification, serviceProvider, notificationPublisher, cancellationToken);
    }

    private static RequestHandlerWrapperBase CreateRequestWrapper(Type requestType, Type responseType) =>
        (RequestHandlerWrapperBase)Activator.CreateInstance(
            typeof(RequestHandlerWrapperImpl<,>).MakeGenericType(requestType, responseType))!;

    private static Type GetResponseType(Type requestType)
    {
        Type? closedInterface = Array.Find(
            requestType.GetInterfaces(),
            i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));

        return closedInterface?.GetGenericArguments()[0]
               ?? throw new ArgumentException(
                   $"Type '{requestType.FullName}' does not implement IRequest<TResponse>.",
                   nameof(requestType));
    }
}
