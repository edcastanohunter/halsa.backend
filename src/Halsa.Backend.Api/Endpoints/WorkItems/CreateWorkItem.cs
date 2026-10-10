using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.WorkItems.Commands.Create;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Api.Endpoints.WorkItems;

internal sealed class CreateWorkItem : IEndpoint
{
    internal sealed record Request(string Title, string? Description);

    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("work-items", async (
                Request request,
                ISender sender,
                CancellationToken cancellationToken) =>
            {
                Result<Guid> result = await sender.Send(
                    new CreateWorkItemCommand(request.Title, request.Description),
                    cancellationToken);

                return result.Match(
                    id => Results.Created($"/work-items/{id}", new { id }),
                    _ => CustomResults.Problem(result));
            })
            .WithTags(Tags.WorkItems)
            .WithName("CreateWorkItem")
            .WithSummary("Creates a work item.")
            .Produces<object>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequirePermission(Permissions.WorkItemsWrite);
}
