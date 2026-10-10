using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.WorkItems.Queries;
using Halsa.Backend.Application.WorkItems.Queries.List;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Api.Endpoints.WorkItems;

internal sealed class ListWorkItems : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("work-items", async (
                ISender sender,
                CancellationToken cancellationToken,
                bool includeCompleted = false,
                int page = 1,
                int pageSize = 20) =>
            {
                Result<PagedList<WorkItemResponse>> result = await sender.Send(
                    new ListWorkItemsQuery(includeCompleted, page, pageSize),
                    cancellationToken);

                return result.Match(Results.Ok, _ => CustomResults.Problem(result));
            })
            .WithTags(Tags.WorkItems)
            .WithName("ListWorkItems")
            .WithSummary("Returns a page of work items.")
            .Produces<PagedList<WorkItemResponse>>()
            .RequirePermission(Permissions.WorkItemsRead);
}
