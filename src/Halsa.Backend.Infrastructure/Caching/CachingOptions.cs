namespace Halsa.Backend.Infrastructure.Caching;

public sealed class CachingOptions
{
    public const string SectionName = "Caching";

    /// <summary>Distributed (Redis) lifetime when a query does not set its own.</summary>
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Upper bound for the in-process copy. Tag evictions reach the other replicas through Redis,
    /// but an entry already in another replica's memory can live this long, so keep it short.
    /// </summary>
    public TimeSpan LocalCacheExpiration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Larger payloads are not cached at all; HybridCache logs and skips them.</summary>
    public long MaximumPayloadBytes { get; set; } = 1024 * 1024;
}
