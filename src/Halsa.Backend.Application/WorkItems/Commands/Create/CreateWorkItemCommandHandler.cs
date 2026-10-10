using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.WorkItems;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.EventSourcing;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.WorkItems.Commands.Create;

internal sealed class CreateWorkItemCommandHandler(
    IEventSourcedRepository<WorkItem, WorkItemId> repository,
    IClock clock)
    : ICommandHandler<CreateWorkItemCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateWorkItemCommand command, CancellationToken cancellationToken)
    {
        Result<WorkItem> creation = WorkItem.Create(
            WorkItemId.New(),
            command.Title,
            command.Description,
            clock.UtcNow);

        if (creation.IsFailure)
        {
            return Result.Failure<Guid>(creation.Error);
        }

        WorkItem workItem = creation.Value;

        await repository.SaveAsync(workItem, cancellationToken).ConfigureAwait(false);

        return workItem.Id.Value;
    }
}
