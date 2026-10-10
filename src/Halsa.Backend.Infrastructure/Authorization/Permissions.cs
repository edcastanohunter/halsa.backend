namespace Halsa.Backend.Infrastructure.Authorization;

/// <summary>
/// Permission names used by <c>RequirePermission</c>. Keep them coarse and business-shaped
/// (<c>work-items:write</c>), not CRUD-shaped, so they survive refactors.
/// </summary>
public static class Permissions
{
    public const string WorkItemsRead = "work-items:read";

    public const string WorkItemsWrite = "work-items:write";
}
