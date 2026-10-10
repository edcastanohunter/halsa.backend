using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.WorkItems;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.WorkItems.Queries.GetById;

internal sealed class GetWorkItemByIdQueryHandler(IReadDbContext readDbContext)
    : IQueryHandler<GetWorkItemByIdQuery, WorkItemResponse>
{
    public async Task<Result<WorkItemResponse>> Handle(
        GetWorkItemByIdQuery query,
        CancellationToken cancellationToken)
    {
        WorkItemResponse? workItem = await readDbContext.WorkItems
            .Where(view => view.Id == query.WorkItemId)
            .Select(view => new WorkItemResponse(
                view.Id,
                view.Title,
                view.Description,
                view.Status,
                view.CreatedOnUtc,
                view.CompletedOnUtc))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return workItem is null
            ? Result.Failure<WorkItemResponse>(WorkItemErrors.NotFound(new WorkItemId(query.WorkItemId)))
            : Result.Success(workItem);
    }
}
