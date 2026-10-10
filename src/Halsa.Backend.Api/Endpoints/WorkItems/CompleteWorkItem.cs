using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.WorkItems.Commands.Complete;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Api.Endpoints.WorkItems;

internal sealed class CompleteWorkItem : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("work-items/{id:guid}/complete", async (
                Guid id,
                ISender sender,
                CancellationToken cancellationToken) =>
            {
                Result result = await sender.Send(new CompleteWorkItemCommand(id), cancellationToken);

                return result.Match(Results.NoContent, _ => CustomResults.Problem(result));
            })
            .WithTags(Tags.WorkItems)
            .WithName("CompleteWorkItem")
            .WithSummary("Marks a work item as completed.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(Permissions.WorkItemsWrite);
}
