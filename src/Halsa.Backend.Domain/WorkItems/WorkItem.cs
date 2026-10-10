using Halsa.Backend.SharedKernel.EventSourcing;
using Halsa.Backend.SharedKernel.Primitives;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Domain.WorkItems;

/// <summary>
/// Reference aggregate for the template: event sourced, invariants enforced in the behaviour
/// methods, state mutated only in <see cref="Apply"/>.
/// <para>Delete this folder once you have modelled your own domain.</para>
/// </summary>
public sealed class WorkItem : EventSourcedAggregateRoot<WorkItemId>
{
    public const int TitleMaxLength = 200;

    /// <summary>Required for replay: the stream, not the constructor, decides the state.</summary>
    private WorkItem()
    {
    }

    public string Title { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public WorkItemStatus Status { get; private set; }

    public DateTime CreatedOnUtc { get; private set; }

    public DateTime? CompletedOnUtc { get; private set; }

    public static Result<WorkItem> Create(WorkItemId id, string title, string? description, DateTime utcNow)
    {
        Result titleCheck = ValidateTitle(title);

        if (titleCheck.IsFailure)
        {
            return Result.Failure<WorkItem>(titleCheck.Error);
        }

        var workItem = new WorkItem();

        workItem.RaiseAndApply(new WorkItemCreated(id.Value, title.Trim(), description?.Trim(), utcNow));

        return workItem;
    }

    public Result Rename(string title)
    {
        Result titleCheck = ValidateTitle(title);

        if (titleCheck.IsFailure)
        {
            return titleCheck;
        }

        if (string.Equals(Title, title.Trim(), StringComparison.Ordinal))
        {
            // Idempotent: renaming to the current title is a no-op, not an error, and must not
            // append a redundant event to the stream.
            return Result.Success();
        }

        RaiseAndApply(new WorkItemRenamed(Id.Value, title.Trim()));

        return Result.Success();
    }

    public Result Complete(DateTime utcNow)
    {
        if (Status is WorkItemStatus.Completed)
        {
            return Result.Failure(WorkItemErrors.AlreadyCompleted(Id));
        }

        RaiseAndApply(new WorkItemCompleted(Id.Value, utcNow));

        return Result.Success();
    }

    protected override void Apply(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case WorkItemCreated created:
                Id = new WorkItemId(created.WorkItemId);
                Title = created.Title;
                Description = created.Description;
                Status = WorkItemStatus.Open;
                CreatedOnUtc = created.CreatedOnUtc;
                break;

            case WorkItemRenamed renamed:
                Title = renamed.Title;
                break;

            case WorkItemCompleted completed:
                Status = WorkItemStatus.Completed;
                CompletedOnUtc = completed.CompletedOnUtc;
                break;

            default:
                throw new DomainException(
                    $"Event '{domainEvent.GetType().Name}' does not belong to the {nameof(WorkItem)} stream.");
        }
    }

    private static Result ValidateTitle(string title) => title switch
    {
        null or "" => Result.Failure(WorkItemErrors.TitleRequired),
        _ when string.IsNullOrWhiteSpace(title) => Result.Failure(WorkItemErrors.TitleRequired),
        _ when title.Trim().Length > TitleMaxLength => Result.Failure(WorkItemErrors.TitleTooLong),
        _ => Result.Success()
    };
}
