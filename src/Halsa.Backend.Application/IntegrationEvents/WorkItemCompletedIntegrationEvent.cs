using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Application.IntegrationEvents;

/// <summary>
/// Public contract: other modules and services may subscribe to this. Add fields, never remove them,
/// and version the type name when the shape has to change.
/// </summary>
public sealed record WorkItemCompletedIntegrationEvent(Guid WorkItemId, DateTime CompletedOnUtc)
    : IntegrationEvent;
