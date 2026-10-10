namespace Halsa.Backend.Application.WorkItems.Queries;

public sealed record WorkItemResponse(
    Guid Id,
    string Title,
    string? Description,
    string Status,
    DateTime CreatedOnUtc,
    DateTime? CompletedOnUtc);
