using System.Text.Json;
using Halsa.Backend.Application.Abstractions.Caching;
using Microsoft.Extensions.Caching.Distributed;

namespace Halsa.Backend.Infrastructure.Caching;

/// <summary>
/// <see cref="IDistributedCache"/>-backed cache. The registration decides whether that is Redis or
/// the in-memory distributed cache; callers cannot tell and should not care.
/// </summary>
internal sealed class CacheService(IDistributedCache distributedCache) : ICacheService
{
    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = await distributedCache.GetAsync(key, cancellationToken).ConfigureAwait(false);

        return bytes is null ? default : JsonSerializer.Deserialize<T>(bytes, SerializerOptions);
    }

    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);

        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration ?? DefaultExpiration
        };

        return distributedCache.SetAsync(key, bytes, options, cancellationToken);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        distributedCache.RemoveAsync(key, cancellationToken);
}
