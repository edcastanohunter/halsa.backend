using Halsa.Backend.Application;
using Halsa.Backend.Application.Abstractions.Caching;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.Behaviors;
using Halsa.Backend.Application.WorkItems;
using Halsa.Backend.Application.WorkItems.Commands.Complete;
using Halsa.Backend.Application.WorkItems.Commands.Create;
using Halsa.Backend.Application.WorkItems.Commands.Rename;
using Halsa.Backend.Application.WorkItems.Queries;
using Halsa.Backend.Application.WorkItems.Queries.GetById;
using Halsa.Backend.Application.WorkItems.Queries.List;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;

namespace Halsa.Backend.UnitTests.WorkItems;

/// <summary>
/// A command that forgets a tag leaves a stale entry behind, and nothing else would notice:
/// these tests pin which tags each write evicts and which tags each read carries.
/// </summary>
public sealed class WorkItemCachingTests
{
    private static readonly Guid Id = Guid.CreateVersion7();

    [Fact]
    public void Create_ShouldEvictTheLists_ButNoSingleItem()
    {
        new CreateWorkItemCommand("t", null).CacheTagsToInvalidate
            .ShouldBe([WorkItemCacheTags.Lists]);
    }

    [Fact]
    public void RenameAndComplete_ShouldEvictTheListsAndTheirOwnItem()
    {
        string[] expected = [WorkItemCacheTags.Lists, WorkItemCacheTags.ById(Id)];

        new RenameWorkItemCommand(Id, "t").CacheTagsToInvalidate.ShouldBe(expected, ignoreOrder: true);
        new CompleteWorkItemCommand(Id).CacheTagsToInvalidate.ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public void EveryCachedRead_ShouldCarryATagThatSomeWriteEvicts()
    {
        new ListWorkItemsQuery().CacheTags.ShouldBe([WorkItemCacheTags.Lists]);
        new GetWorkItemByIdQuery(Id).CacheTags.ShouldBe([WorkItemCacheTags.ById(Id)]);
    }

    [Fact]
    public void CacheKeys_ShouldDifferByEveryParameter_AndNeverCollideBetweenListAndItem()
    {
        string[] keys =
        [
            new ListWorkItemsQuery(IncludeCompleted: false, Page: 1, PageSize: 20).CacheKey,
            new ListWorkItemsQuery(IncludeCompleted: true, Page: 1, PageSize: 20).CacheKey,
            new ListWorkItemsQuery(IncludeCompleted: false, Page: 2, PageSize: 20).CacheKey,
            new ListWorkItemsQuery(IncludeCompleted: false, Page: 1, PageSize: 50).CacheKey,
            new GetWorkItemByIdQuery(Id).CacheKey
        ];

        keys.Distinct().Count().ShouldBe(keys.Length);
    }

    [Fact]
    public void AddApplication_ShouldPutCommandsThroughInvalidation_AndQueriesThroughCaching()
    {
        ServiceProvider provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Substitute.For<ICacheService>())
            .AddApplication()
            .BuildServiceProvider();

        Type[] commandBehaviors = BehaviorTypes<CreateWorkItemCommand, Result<Guid>>(provider);
        Type[] queryBehaviors = BehaviorTypes<ListWorkItemsQuery, Result<PagedList<WorkItemResponse>>>(provider);

        commandBehaviors.ShouldContain(typeof(CacheInvalidationBehavior<CreateWorkItemCommand, Result<Guid>>));
        commandBehaviors.ShouldNotContain(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(QueryCachingBehavior<,>));
        queryBehaviors.ShouldContain(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(QueryCachingBehavior<,>));
        queryBehaviors.ShouldNotContain(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(CacheInvalidationBehavior<,>));
    }

    private static Type[] BehaviorTypes<TRequest, TResponse>(IServiceProvider provider)
        where TRequest : IRequest<TResponse> =>
        [.. provider.GetServices<IPipelineBehavior<TRequest, TResponse>>().Select(behavior => behavior.GetType())];
}
