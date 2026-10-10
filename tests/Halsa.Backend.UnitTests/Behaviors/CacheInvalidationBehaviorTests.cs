using Halsa.Backend.Application.Abstractions.Caching;
using Halsa.Backend.Application.Behaviors;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.Extensions.Logging.Abstractions;

namespace Halsa.Backend.UnitTests.Behaviors;

public sealed class CacheInvalidationBehaviorTests
{
    public sealed record WriteCommand : IRequest<Result>, IInvalidatesCache
    {
        public IReadOnlyCollection<string> CacheTagsToInvalidate => ["lists"];
    }

    private readonly InMemoryCacheService _cache = new();

    private CacheInvalidationBehavior<WriteCommand, Result> CreateBehavior() =>
        new(_cache, NullLogger<CacheInvalidationBehavior<WriteCommand, Result>>.Instance);

    private async Task SeedAsync()
    {
        await _cache.GetOrCreateAsync("list", _ => ValueTask.FromResult("old"), tags: ["lists"]);
        await _cache.GetOrCreateAsync("other", _ => ValueTask.FromResult("kept"), tags: ["other"]);
    }

    [Fact]
    public async Task Handle_ShouldEvictOnlyTheDeclaredTags_WhenTheCommandSucceeds()
    {
        await SeedAsync();

        await CreateBehavior().Handle(new WriteCommand(), _ => Task.FromResult(Result.Success()), CancellationToken.None);

        _cache.Count.ShouldBe(1);
        string kept = await _cache.GetOrCreateAsync("other", _ => ValueTask.FromResult("recomputed"));
        kept.ShouldBe("kept");
    }

    [Fact]
    public async Task Handle_ShouldEvictNothing_WhenTheCommandFails()
    {
        await SeedAsync();

        await CreateBehavior().Handle(
            new WriteCommand(),
            _ => Task.FromResult(Result.Failure(Error.Conflict("X.Conflict", "nope"))),
            CancellationToken.None);

        _cache.Count.ShouldBe(2);
    }
}
