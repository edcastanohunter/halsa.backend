using Halsa.Backend.Application.Abstractions.Messaging;

namespace Halsa.Backend.Application.WorkItems.Queries.GetById;

public sealed record GetWorkItemByIdQuery(Guid WorkItemId) : IQuery<WorkItemResponse>;
