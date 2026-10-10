using Halsa.Backend.Application.Abstractions.Caching;
using Halsa.Backend.Application.Behaviors;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.Extensions.Logging.Abstractions;

namespace Halsa.Backend.UnitTests.Behaviors;

public sealed class QueryCachingBehaviorTests
{
    private sealed record CachedQuery : ICachedQuery<string>
    {
        public string CacheKey => "cached-query";

        public TimeSpan? Expiration => TimeSpan.FromMinutes(1);
    }

    [Fact]
    public async Task Handle_ShouldReturnTheCachedValue_WithoutCallingTheHandler()
    {
        ICacheService cache = Substitute.For<ICacheService>();
        Result<string> cached = Result.Success("from-cache");

        cache.GetAsync<Result<string>>("cached-query", Arg.Any<CancellationToken>()).Returns(cached);

        var behavior = new QueryCachingBehavior<CachedQuery, Result<string>>(
            cache,
            NullLogger<QueryCachingBehavior<CachedQuery, Result<string>>>.Instance);

        var handlerCalled = false;

        Result<string> result = await behavior.Handle(
            new CachedQuery(),
            _ =>
            {
                handlerCalled = true;
                return Task.FromResult(Result.Success("from-handler"));
            },
            CancellationToken.None);

        handlerCalled.ShouldBeFalse();
        result.Value.ShouldBe("from-cache");
    }

    [Fact]
    public async Task Handle_ShouldNotCacheFailures()
    {
        ICacheService cache = Substitute.For<ICacheService>();

        cache.GetAsync<Result<string>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Result<string>?)null);

        var behavior = new QueryCachingBehavior<CachedQuery, Result<string>>(
            cache,
            NullLogger<QueryCachingBehavior<CachedQuery, Result<string>>>.Instance);

        await behavior.Handle(
            new CachedQuery(),
            _ => Task.FromResult(Result.Failure<string>(Error.NotFound("X.NotFound", "missing"))),
            CancellationToken.None);

        await cache.DidNotReceive().SetAsync(
            Arg.Any<string>(),
            Arg.Any<Result<string>>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }
}
