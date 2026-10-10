using Halsa.Backend.Application.Abstractions.Caching;

namespace Halsa.Backend.UnitTests.Behaviors;

/// <summary>Minimal tag-aware cache with the same contract as the HybridCache-backed service.</summary>
internal sealed class InMemoryCacheService : ICacheService
{
    private readonly Dictionary<string, (object? Value, IReadOnlyCollection<string> Tags)> _entries = [];

    public int Count => _entries.Count;

    public async ValueTask<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        TimeSpan? expiration = null,
        IReadOnlyCollection<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        if (_entries.TryGetValue(key, out (object? Value, IReadOnlyCollection<string> Tags) entry))
        {
            return (T)entry.Value!;
        }

        T value = await factory(cancellationToken);
        _entries[key] = (value, tags ?? []);

        return value;
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _entries.Remove(key);
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveByTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default)
    {
        foreach (string key in _entries.Where(e => e.Value.Tags.Intersect(tags).Any()).Select(e => e.Key).ToList())
        {
            _entries.Remove(key);
        }

        return ValueTask.CompletedTask;
    }
}
