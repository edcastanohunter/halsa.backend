using FluentValidation;

namespace Halsa.Backend.Application.WorkItems.Commands.Complete;

internal sealed class CompleteWorkItemCommandValidator : AbstractValidator<CompleteWorkItemCommand>
{
    public CompleteWorkItemCommandValidator() => RuleFor(command => command.WorkItemId).NotEmpty();
}
