using System.Collections.Concurrent;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.WorkItems.Queries;
using Halsa.Backend.Infrastructure.Caching;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.UnitTests.Caching;

/// <summary>
/// Runs the real HybridCache. Two "replicas" share one distributed store, which stands in for Redis,
/// so values cross the serializer exactly as they would between API instances.
/// </summary>
public sealed class CacheServiceTests
{
    // Not MemoryDistributedCache: HybridCache deliberately ignores it as L2 because it duplicates L1.
    private readonly SharedDistributedCache _sharedL2 = new();

    private static CacheService CreateReplica(IDistributedCache l2)
    {
        ServiceProvider provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(l2)
            .AddHybridCache()
            .Services
            .BuildServiceProvider();

        return new CacheService(
            provider.GetRequiredService<HybridCache>(),
            Options.Create(new CachingOptions()),
            NullLogger<CacheService>.Instance);
    }

    private static Result<PagedList<WorkItemResponse>> Page(string title) =>
        Result.Success(new PagedList<WorkItemResponse>(
            [new WorkItemResponse(Guid.NewGuid(), title, null, "Open", DateTime.UtcNow, null)], 1, 20, 1));

    [Fact]
    public async Task AResultCachedByOneReplica_ShouldDeserializeOnAnother()
    {
        CacheService first = CreateReplica(_sharedL2);
        CacheService second = CreateReplica(_sharedL2);

        await first.GetOrCreateAsync("list", _ => ValueTask.FromResult(Page("cached")), tags: ["lists"]);

        Result<PagedList<WorkItemResponse>> fromL2 = await second.GetOrCreateAsync(
            "list", _ => ValueTask.FromResult(Page("recomputed")), tags: ["lists"]);

        fromL2.IsSuccess.ShouldBeTrue();
        fromL2.Value.Items[0].Title.ShouldBe("cached");
    }

    [Fact]
    public async Task RemovingATag_ShouldMakeAFreshReplicaRecompute()
    {
        CacheService writer = CreateReplica(_sharedL2);

        await writer.GetOrCreateAsync("list", _ => ValueTask.FromResult(Page("stale")), tags: ["lists"]);
        await writer.RemoveByTagsAsync(["lists"]);

        CacheService reader = CreateReplica(_sharedL2);
        Result<PagedList<WorkItemResponse>> afterEviction = await reader.GetOrCreateAsync(
            "list", _ => ValueTask.FromResult(Page("fresh")), tags: ["lists"]);

        afterEviction.Value.Items[0].Title.ShouldBe("fresh");
    }

    [Fact]
    public async Task RemovingATag_ShouldEvictTheLocalCopyOnTheSameReplica()
    {
        CacheService replica = CreateReplica(_sharedL2);

        await replica.GetOrCreateAsync("list", _ => ValueTask.FromResult("stale"), tags: ["lists"]);
        await replica.RemoveByTagsAsync(["lists"]);

        string value = await replica.GetOrCreateAsync("list", _ => ValueTask.FromResult("fresh"), tags: ["lists"]);

        value.ShouldBe("fresh");
    }

    [Fact]
    public async Task ADistributedCacheOutage_ShouldFallBackToTheFactory()
    {
        CacheService replica = CreateReplica(new BrokenDistributedCache());

        string value = await replica.GetOrCreateAsync("key", _ => ValueTask.FromResult("from-database"), tags: ["t"]);
        await replica.RemoveByTagsAsync(["t"]);

        value.ShouldBe("from-database");
    }

    /// <summary>Process-wide byte store standing in for Redis. Expiration is irrelevant to these tests.</summary>
    private sealed class SharedDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _store = new();

        public byte[]? Get(string key) => _store.GetValueOrDefault(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _store[key] = value;

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => _store.TryRemove(key, out _);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class BrokenDistributedCache : IDistributedCache
    {
        private static InvalidOperationException Down() => new("Redis is down.");

        public byte[]? Get(string key) => throw Down();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw Down();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw Down();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw Down();
        public void Refresh(string key) => throw Down();
        public Task RefreshAsync(string key, CancellationToken token = default) => throw Down();
        public void Remove(string key) => throw Down();
        public Task RemoveAsync(string key, CancellationToken token = default) => throw Down();
    }
}
