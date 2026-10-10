using Halsa.Backend.Application.Abstractions.Caching;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.Infrastructure.Caching;

/// <summary>
/// <see cref="HybridCache"/>-backed cache: in-process L1, Redis L2 when configured, stampede
/// protection and tag eviction. HybridCache already treats L2 failures as misses, so a Redis outage
/// degrades to the database instead of failing requests.
/// </summary>
internal sealed class CacheService(
    HybridCache hybridCache,
    IOptions<CachingOptions> options,
    ILogger<CacheService> logger) : ICacheService
{
    private readonly CachingOptions _options = options.Value;

    public ValueTask<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        TimeSpan? expiration = null,
        IReadOnlyCollection<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan distributed = expiration ?? _options.DefaultExpiration;

        var entryOptions = new HybridCacheEntryOptions
        {
            Expiration = distributed,
            LocalCacheExpiration = distributed < _options.LocalCacheExpiration
                ? distributed
                : _options.LocalCacheExpiration
        };

        return hybridCache.GetOrCreateAsync(key, factory, entryOptions, tags, cancellationToken);
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        hybridCache.RemoveAsync(key, cancellationToken);

    public async ValueTask RemoveByTagsAsync(
        IReadOnlyCollection<string> tags,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await hybridCache.RemoveByTagAsync(tags, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The write has committed; failing the request now would lie about it.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            logger.LogWarning(
                exception,
                "Could not invalidate cache tags {CacheTags}; entries expire on their own",
                tags);
        }
    }
}
