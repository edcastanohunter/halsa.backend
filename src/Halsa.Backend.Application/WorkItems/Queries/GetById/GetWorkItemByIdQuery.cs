using Halsa.Backend.Application.Abstractions.Caching;

namespace Halsa.Backend.Application.WorkItems.Queries.GetById;

/// <summary>A "not found" result is a failure, so it is never cached and a later create is seen at once.</summary>
public sealed record GetWorkItemByIdQuery(Guid WorkItemId) : ICachedQuery<WorkItemResponse>
{
    public string CacheKey => $"work-items:{WorkItemId}";

    public TimeSpan? Expiration => TimeSpan.FromMinutes(5);

    public IReadOnlyCollection<string> CacheTags => [WorkItemCacheTags.ById(WorkItemId)];
}
