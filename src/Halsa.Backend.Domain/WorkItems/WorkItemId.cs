namespace Halsa.Backend.Domain.WorkItems;

/// <summary>
/// Strongly typed identifier. Makes it impossible to pass a customer id where a work item id
/// is expected, and gives EF Core a single conversion point.
/// </summary>
public readonly record struct WorkItemId(Guid Value)
{
    /// <summary>UUID v7 keeps inserts sequential, which matters for index locality in PostgreSQL.</summary>
    public static WorkItemId New() => new(Guid.CreateVersion7());

    public static WorkItemId Empty => new(Guid.Empty);

    public override string ToString() => Value.ToString();
}
