using Halsa.Backend.Application.Abstractions.Messaging;

namespace Halsa.Backend.Application.WorkItems.Commands.Rename;

public sealed record RenameWorkItemCommand(Guid WorkItemId, string Title) : ICommand;
