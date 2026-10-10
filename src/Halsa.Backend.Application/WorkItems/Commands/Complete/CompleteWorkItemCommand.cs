using Halsa.Backend.Application.Abstractions.Caching;
using Halsa.Backend.Application.Abstractions.Messaging;

namespace Halsa.Backend.Application.WorkItems.Commands.Complete;

public sealed record CompleteWorkItemCommand(Guid WorkItemId) : ICommand, IInvalidatesCache
{
    public IReadOnlyCollection<string> CacheTagsToInvalidate =>
        [WorkItemCacheTags.Lists, WorkItemCacheTags.ById(WorkItemId)];
}
