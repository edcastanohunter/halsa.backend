using Halsa.Backend.Application.Abstractions.Caching;
using Halsa.Backend.Application.Abstractions.Messaging;

namespace Halsa.Backend.Application.WorkItems.Commands.Rename;

public sealed record RenameWorkItemCommand(Guid WorkItemId, string Title) : ICommand, IInvalidatesCache
{
    public IReadOnlyCollection<string> CacheTagsToInvalidate =>
        [WorkItemCacheTags.Lists, WorkItemCacheTags.ById(WorkItemId)];
}
