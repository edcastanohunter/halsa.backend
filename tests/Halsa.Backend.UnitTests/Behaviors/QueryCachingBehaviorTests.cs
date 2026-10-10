using Halsa.Backend.Application.Abstractions.Caching;
using Halsa.Backend.Application.Behaviors;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.Extensions.Logging.Abstractions;

namespace Halsa.Backend.UnitTests.Behaviors;

public sealed class QueryCachingBehaviorTests
{
    public sealed record CachedQuery : ICachedQuery<string>
    {
        public string CacheKey => "cached-query";

        public TimeSpan? Expiration => TimeSpan.FromMinutes(1);

        public IReadOnlyCollection<string> CacheTags => ["sample"];
    }

    private readonly InMemoryCacheService _cache = new();

    private QueryCachingBehavior<CachedQuery, Result<string>> CreateBehavior() =>
        new(_cache, NullLogger<QueryCachingBehavior<CachedQuery, Result<string>>>.Instance);

    [Fact]
    public async Task Handle_ShouldServeTheSecondCallFromCache_WithoutCallingTheHandler()
    {
        QueryCachingBehavior<CachedQuery, Result<string>> behavior = CreateBehavior();
        var calls = 0;

        Task<Result<string>> Handler(CancellationToken _)
        {
            calls++;
            return Task.FromResult(Result.Success($"call-{calls}"));
        }

        Result<string> first = await behavior.Handle(new CachedQuery(), Handler, CancellationToken.None);
        Result<string> second = await behavior.Handle(new CachedQuery(), Handler, CancellationToken.None);

        calls.ShouldBe(1);
        first.Value.ShouldBe("call-1");
        second.Value.ShouldBe("call-1");
    }

    [Fact]
    public async Task Handle_ShouldReturnFailuresUnchanged_AndNotCacheThem()
    {
        QueryCachingBehavior<CachedQuery, Result<string>> behavior = CreateBehavior();
        Error notFound = Error.NotFound("X.NotFound", "missing");

        Result<string> result = await behavior.Handle(
            new CachedQuery(),
            _ => Task.FromResult(Result.Failure<string>(notFound)),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(notFound);
        _cache.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Handle_ShouldCallTheHandlerAgain_AfterItsTagIsInvalidated()
    {
        QueryCachingBehavior<CachedQuery, Result<string>> behavior = CreateBehavior();
        var calls = 0;

        Task<Result<string>> Handler(CancellationToken _) => Task.FromResult(Result.Success($"call-{++calls}"));

        await behavior.Handle(new CachedQuery(), Handler, CancellationToken.None);
        await _cache.RemoveByTagsAsync(["sample"]);
        Result<string> afterEviction = await behavior.Handle(new CachedQuery(), Handler, CancellationToken.None);

        afterEviction.Value.ShouldBe("call-2");
    }
}
