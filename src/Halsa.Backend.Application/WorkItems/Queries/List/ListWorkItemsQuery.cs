using Halsa.Backend.Application.Abstractions.Caching;
using Halsa.Backend.Application.Abstractions.Models;

namespace Halsa.Backend.Application.WorkItems.Queries.List;

/// <summary>
/// Opts into the caching behaviour: list endpoints are read-heavy and tolerate a short staleness
/// window. Drop <see cref="ICachedQuery{TResponse}"/> for a query that must always be fresh.
/// </summary>
public sealed record ListWorkItemsQuery(bool IncludeCompleted = false, int Page = 1, int PageSize = 20)
    : ICachedQuery<PagedList<WorkItemResponse>>
{
    public string CacheKey => $"work-items:{IncludeCompleted}:{Page}:{PageSize}";

    public TimeSpan? Expiration => TimeSpan.FromSeconds(30);
}
