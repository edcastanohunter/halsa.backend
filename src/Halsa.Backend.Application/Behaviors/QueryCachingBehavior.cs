using Halsa.Backend.Application.Abstractions.Caching;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.Extensions.Logging;

namespace Halsa.Backend.Application.Behaviors;

/// <summary>
/// Serves queries that opt in via <see cref="ICachedQuery"/> from the distributed cache.
/// Only successful results are cached: a miss must never be poisoned by a transient failure.
/// </summary>
public sealed class QueryCachingBehavior<TRequest, TResponse>(
    ICacheService cacheService,
    ILogger<QueryCachingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, ICachedQuery
    where TResponse : Result
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        TResponse? cached = await cacheService
            .GetAsync<TResponse>(request.CacheKey, cancellationToken)
            .ConfigureAwait(false);

        if (cached is not null)
        {
            logger.LogDebug("Cache hit for {CacheKey}", request.CacheKey);
            return cached;
        }

        TResponse response = await next(cancellationToken).ConfigureAwait(false);

        if (response.IsSuccess)
        {
            await cacheService
                .SetAsync(request.CacheKey, response, request.Expiration, cancellationToken)
                .ConfigureAwait(false);
        }

        return response;
    }
}
