using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.WorkItems;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.EventSourcing;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.WorkItems.Commands.Complete;

internal sealed class CompleteWorkItemCommandHandler(
    IEventSourcedRepository<WorkItem, WorkItemId> repository,
    IClock clock)
    : ICommandHandler<CompleteWorkItemCommand>
{
    public async Task<Result> Handle(CompleteWorkItemCommand command, CancellationToken cancellationToken)
    {
        var id = new WorkItemId(command.WorkItemId);

        WorkItem? workItem = await repository.LoadAsync(id, cancellationToken).ConfigureAwait(false);

        if (workItem is null)
        {
            return Result.Failure(WorkItemErrors.NotFound(id));
        }

        Result completion = workItem.Complete(clock.UtcNow);

        if (completion.IsFailure)
        {
            return completion;
        }

        await repository.SaveAsync(workItem, cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
