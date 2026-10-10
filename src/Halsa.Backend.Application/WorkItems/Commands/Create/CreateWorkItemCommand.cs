using Halsa.Backend.Application.Abstractions.Messaging;

namespace Halsa.Backend.Application.WorkItems.Commands.Create;

public sealed record CreateWorkItemCommand(string Title, string? Description) : ICommand<Guid>;
