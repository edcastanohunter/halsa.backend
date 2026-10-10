using Microsoft.Extensions.DependencyInjection;

namespace Halsa.Backend.Mediation.Internal;

internal abstract class RequestHandlerWrapperBase
{
    public abstract Task<object?> HandleUntyped(
        object request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}

internal abstract class RequestHandlerWrapper<TResponse> : RequestHandlerWrapperBase
{
    public abstract Task<TResponse> Handle(
        IRequest<TResponse> request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}

/// <summary>
/// Closes the open generics once per request type so that dispatch after the first call is a
/// dictionary lookup plus a virtual call - no reflection on the hot path.
/// </summary>
internal sealed class RequestHandlerWrapperImpl<TRequest, TResponse> : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    public override async Task<object?> HandleUntyped(
        object request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken) =>
        await Handle((IRequest<TResponse>)request, serviceProvider, cancellationToken).ConfigureAwait(false);

    public override Task<TResponse> Handle(
        IRequest<TResponse> request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;

        Task<TResponse> HandlerCore(CancellationToken token = default)
        {
            IRequestHandler<TRequest, TResponse> handler =
                serviceProvider.GetService<IRequestHandler<TRequest, TResponse>>()
                ?? throw new HandlerNotFoundException(typeof(TRequest));

            return handler.Handle(typedRequest, token == default ? cancellationToken : token);
        }

        // Behaviours are applied in reverse so that the first registered behaviour is outermost.
        RequestHandlerDelegate<TResponse> pipeline = serviceProvider
            .GetServices<IPipelineBehavior<TRequest, TResponse>>()
            .Reverse()
            .Aggregate(
                (RequestHandlerDelegate<TResponse>)HandlerCore,
                (next, behavior) => token => behavior.Handle(
                    typedRequest,
                    next,
                    token == default ? cancellationToken : token));

        return pipeline(cancellationToken);
    }
}
