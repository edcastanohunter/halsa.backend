namespace Halsa.Backend.Application.Abstractions.Caching;

/// <summary>
/// Two-level cache (in-process + distributed) seen from the application layer. Entries are grouped
/// by tags so that a write can invalidate every entry it affects without knowing their keys.
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// Returns the cached value or runs <paramref name="factory"/> once per key, even under concurrent
    /// misses. An exception thrown by the factory reaches every waiting caller and nothing is cached.
    /// </summary>
    ValueTask<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        TimeSpan? expiration = null,
        IReadOnlyCollection<string>? tags = null,
        CancellationToken cancellationToken = default);

    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Best effort: a cache outage is logged, never surfaced, because the write already committed.</summary>
    ValueTask RemoveByTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default);
}
