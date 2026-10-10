namespace Halsa.Backend.Application.Abstractions.Caching;

/// <summary>
/// Distributed cache seen from the application layer. The implementation decides whether that is
/// Redis or an in-memory store; nothing here knows the difference.
/// </summary>
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
