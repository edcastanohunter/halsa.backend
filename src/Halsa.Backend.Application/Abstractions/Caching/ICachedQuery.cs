using Halsa.Backend.Application.Abstractions.Messaging;

namespace Halsa.Backend.Application.Abstractions.Caching;

/// <summary>Opt-in marker: a query that the caching behaviour is allowed to serve from cache.</summary>
public interface ICachedQuery
{
    string CacheKey { get; }

    /// <summary>Distributed lifetime. The in-process copy never outlives the configured local expiration.</summary>
    TimeSpan? Expiration { get; }

    /// <summary>Tags a write uses to evict this entry. See <see cref="IInvalidatesCache"/>.</summary>
    IReadOnlyCollection<string> CacheTags { get; }
}

/// <inheritdoc cref="ICachedQuery" />
public interface ICachedQuery<TResponse> : IQuery<TResponse>, ICachedQuery;
