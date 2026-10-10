using Halsa.Backend.SharedKernel.EventSourcing;
using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Domain.WorkItems;

/// <summary>
/// The work item's history. Event names are pinned with <see cref="EventNameAttribute"/> so that
/// renaming a C# type never breaks the ability to replay years of stored events.
/// </summary>
[EventName("work-item.created.v1")]
public sealed record WorkItemCreated(
    Guid WorkItemId,
    string Title,
    string? Description,
    DateTime CreatedOnUtc) : DomainEvent, IPersistedEvent;

[EventName("work-item.renamed.v1")]
public sealed record WorkItemRenamed(
    Guid WorkItemId,
    string Title) : DomainEvent, IPersistedEvent;

[EventName("work-item.completed.v1")]
public sealed record WorkItemCompleted(
    Guid WorkItemId,
    DateTime CompletedOnUtc) : DomainEvent, IPersistedEvent;
