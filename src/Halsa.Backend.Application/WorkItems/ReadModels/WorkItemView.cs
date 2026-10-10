namespace Halsa.Backend.Application.WorkItems.ReadModels;

/// <summary>
/// Denormalised read model, rebuilt by a projection from the work item event stream.
/// It is shaped for the screens that consume it, not for the domain.
/// </summary>
public sealed class WorkItemView
{
    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTime CreatedOnUtc { get; set; }

    public DateTime? CompletedOnUtc { get; set; }

    /// <summary>Stream version this row reflects. Lets a projection skip events it already applied.</summary>
    public int Version { get; set; }
}
