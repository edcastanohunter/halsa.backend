namespace Halsa.Backend.Mediation;

/// <summary>Single owner of the behaviour triggered by <typeparamref name="TRequest"/>.</summary>
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
