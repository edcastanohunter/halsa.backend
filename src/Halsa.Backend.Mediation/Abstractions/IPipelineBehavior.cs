namespace Halsa.Backend.Mediation;

/// <summary>Continuation of the request pipeline.</summary>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(CancellationToken cancellationToken = default);

/// <summary>
/// Cross-cutting concern wrapped around a handler (logging, validation, transactions, caching).
/// Behaviours run in registration order, outermost first, and the innermost one calls the handler.
/// </summary>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
