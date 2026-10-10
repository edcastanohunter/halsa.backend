using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.WorkItems.Commands.Rename;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Api.Endpoints.WorkItems;

internal sealed class RenameWorkItem : IEndpoint
{
    internal sealed record Request(string Title);

    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPut("work-items/{id:guid}/title", async (
                Guid id,
                Request request,
                ISender sender,
                CancellationToken cancellationToken) =>
            {
                Result result = await sender.Send(
                    new RenameWorkItemCommand(id, request.Title),
                    cancellationToken);

                return result.Match(Results.NoContent, _ => CustomResults.Problem(result));
            })
            .WithTags(Tags.WorkItems)
            .WithName("RenameWorkItem")
            .WithSummary("Renames a work item.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(Permissions.WorkItemsWrite);
}
