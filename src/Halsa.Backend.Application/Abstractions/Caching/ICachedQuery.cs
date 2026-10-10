using Halsa.Backend.Application.Abstractions.Messaging;

namespace Halsa.Backend.Application.Abstractions.Caching;

/// <summary>Opt-in marker: a query that the caching behaviour is allowed to serve from cache.</summary>
public interface ICachedQuery
{
    string CacheKey { get; }

    TimeSpan? Expiration { get; }
}

/// <inheritdoc cref="ICachedQuery" />
public interface ICachedQuery<TResponse> : IQuery<TResponse>, ICachedQuery;
