namespace Halsa.Backend.Mediation;

/// <summary>Sends a request through the pipeline to its single handler.</summary>
public interface ISender
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Weakly-typed overload for callers that only hold an <see cref="object"/> (deserialised
    /// messages, dynamic dispatch). Prefer the generic overload everywhere else.
    /// </summary>
    Task<object?> Send(object request, CancellationToken cancellationToken = default);
}
