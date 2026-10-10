using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.WorkItems.Queries;
using Halsa.Backend.Application.WorkItems.Queries.GetById;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Api.Endpoints.WorkItems;

internal sealed class GetWorkItemById : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapGet("work-items/{id:guid}", async (
                Guid id,
                ISender sender,
                CancellationToken cancellationToken) =>
            {
                Result<WorkItemResponse> result = await sender.Send(
                    new GetWorkItemByIdQuery(id),
                    cancellationToken);

                return result.Match(Results.Ok, _ => CustomResults.Problem(result));
            })
            .WithTags(Tags.WorkItems)
            .WithName("GetWorkItemById")
            .WithSummary("Returns a single work item.")
            .Produces<WorkItemResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permissions.WorkItemsRead);
}
