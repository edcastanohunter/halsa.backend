using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.WorkItems.ReadModels;
using Halsa.Backend.Domain.WorkItems;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.WorkItems.Queries.List;

internal sealed class ListWorkItemsQueryHandler(IReadDbContext readDbContext)
    : IQueryHandler<ListWorkItemsQuery, PagedList<WorkItemResponse>>
{
    private const int MaxPageSize = 100;

    public async Task<Result<PagedList<WorkItemResponse>>> Handle(
        ListWorkItemsQuery query,
        CancellationToken cancellationToken)
    {
        int page = Math.Max(query.Page, 1);
        int pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        IQueryable<WorkItemView> source = readDbContext.WorkItems;

        if (!query.IncludeCompleted)
        {
            source = source.Where(view => view.Status != nameof(WorkItemStatus.Completed));
        }

        int totalCount = await source.CountAsync(cancellationToken).ConfigureAwait(false);

        List<WorkItemResponse> items = await source
            .OrderByDescending(view => view.CreatedOnUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(view => new WorkItemResponse(
                view.Id,
                view.Title,
                view.Description,
                view.Status,
                view.CreatedOnUtc,
                view.CompletedOnUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(new PagedList<WorkItemResponse>(items, page, pageSize, totalCount));
    }
}
