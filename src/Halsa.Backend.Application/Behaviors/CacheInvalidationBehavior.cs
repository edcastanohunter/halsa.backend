using Halsa.Backend.Application.Abstractions.Caching;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.Extensions.Logging;

namespace Halsa.Backend.Application.Behaviors;

/// <summary>
/// Evicts the cache tags a command declares through <see cref="IInvalidatesCache"/>, after it succeeds.
/// It must stay outside any behaviour that owns a transaction: evicting before the commit lets a
/// concurrent read cache the old state again.
/// </summary>
public sealed class CacheInvalidationBehavior<TRequest, TResponse>(
    ICacheService cacheService,
    ILogger<CacheInvalidationBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IInvalidatesCache
    where TResponse : Result
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        TResponse response = await next(cancellationToken).ConfigureAwait(false);

        if (response.IsSuccess && request.CacheTagsToInvalidate.Count > 0)
        {
            logger.LogDebug("Invalidating cache tags {CacheTags}", request.CacheTagsToInvalidate);

            await cacheService
                .RemoveByTagsAsync(request.CacheTagsToInvalidate, cancellationToken)
                .ConfigureAwait(false);
        }

        return response;
    }
}
