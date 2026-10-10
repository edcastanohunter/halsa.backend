using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.WorkItems;
using Halsa.Backend.SharedKernel.EventSourcing;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.WorkItems.Commands.Rename;

internal sealed class RenameWorkItemCommandHandler(
    IEventSourcedRepository<WorkItem, WorkItemId> repository)
    : ICommandHandler<RenameWorkItemCommand>
{
    public async Task<Result> Handle(RenameWorkItemCommand command, CancellationToken cancellationToken)
    {
        var id = new WorkItemId(command.WorkItemId);

        WorkItem? workItem = await repository.LoadAsync(id, cancellationToken).ConfigureAwait(false);

        if (workItem is null)
        {
            return Result.Failure(WorkItemErrors.NotFound(id));
        }

        Result rename = workItem.Rename(command.Title);

        if (rename.IsFailure)
        {
            return rename;
        }

        await repository.SaveAsync(workItem, cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
