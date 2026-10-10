using Halsa.Backend.Application.Abstractions.Caching;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.Extensions.Logging;

namespace Halsa.Backend.Application.Behaviors;

/// <summary>
/// Serves queries that opt in via <see cref="ICachedQuery"/> from the cache.
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
        try
        {
            return await cacheService
                .GetOrCreateAsync(
                    request.CacheKey,
                    async token =>
                    {
                        logger.LogDebug("Cache miss for {CacheKey}", request.CacheKey);

                        TResponse response = await next(token).ConfigureAwait(false);

                        // Throwing is the only way to keep a value out of a get-or-create cache.
                        return response.IsSuccess ? response : throw new UncacheableResultException(response);
                    },
                    request.Expiration,
                    request.CacheTags,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UncacheableResultException exception)
        {
            return (TResponse)exception.Result;
        }
    }
}

/// <summary>Carries a failed result out of the cache factory without caching it.</summary>
internal sealed class UncacheableResultException(Result result)
    : Exception("The result is a failure and must not be cached.")
{
    public Result Result { get; } = result;
}
