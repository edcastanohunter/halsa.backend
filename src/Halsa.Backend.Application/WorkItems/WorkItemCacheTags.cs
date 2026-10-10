namespace Halsa.Backend.Application.WorkItems;

/// <summary>
/// Cache tags for work item queries. Lists and single items are tagged separately, so creating an
/// item evicts the lists without touching every cached detail.
/// </summary>
public static class WorkItemCacheTags
{
    public const string Lists = "work-items:lists";

    public static string ById(Guid workItemId) => $"work-items:{workItemId}";
}
