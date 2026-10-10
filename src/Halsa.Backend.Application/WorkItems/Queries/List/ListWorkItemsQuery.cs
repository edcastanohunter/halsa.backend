using Halsa.Backend.Application.Abstractions.Caching;
using Halsa.Backend.Application.Abstractions.Models;

namespace Halsa.Backend.Application.WorkItems.Queries.List;

/// <summary>
/// Opts into the caching behaviour. Every work item command evicts <see cref="WorkItemCacheTags.Lists"/>,
/// so the expiration only bounds staleness when an eviction is lost.
/// </summary>
public sealed record ListWorkItemsQuery(bool IncludeCompleted = false, int Page = 1, int PageSize = 20)
    : ICachedQuery<PagedList<WorkItemResponse>>
{
    public string CacheKey => $"work-items:list:{IncludeCompleted}:{Page}:{PageSize}";

    public TimeSpan? Expiration => TimeSpan.FromMinutes(2);

    public IReadOnlyCollection<string> CacheTags => [WorkItemCacheTags.Lists];
}
