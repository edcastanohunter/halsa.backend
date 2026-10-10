using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Domain.WorkItems;

/// <summary>
/// Every failure this aggregate can produce, in one place. Error codes are part of the API contract:
/// clients switch on them, so treat a rename as a breaking change.
/// </summary>
public static class WorkItemErrors
{
    public static Error NotFound(WorkItemId id) => Error.NotFound(
        "WorkItems.NotFound",
        $"No work item was found with id '{id}'.");

    public static readonly Error TitleRequired = Error.Problem(
        "WorkItems.TitleRequired",
        "A work item must have a title.");

    public static readonly Error TitleTooLong = Error.Problem(
        "WorkItems.TitleTooLong",
        $"A work item title cannot exceed {WorkItem.TitleMaxLength} characters.");

    public static Error AlreadyCompleted(WorkItemId id) => Error.Conflict(
        "WorkItems.AlreadyCompleted",
        $"Work item '{id}' has already been completed.");
}
