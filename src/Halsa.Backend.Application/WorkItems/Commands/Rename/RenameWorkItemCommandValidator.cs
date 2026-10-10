using Halsa.Backend.Domain.WorkItems;
using FluentValidation;

namespace Halsa.Backend.Application.WorkItems.Commands.Rename;

internal sealed class RenameWorkItemCommandValidator : AbstractValidator<RenameWorkItemCommand>
{
    public RenameWorkItemCommandValidator()
    {
        RuleFor(command => command.WorkItemId).NotEmpty();

        RuleFor(command => command.Title)
            .NotEmpty().WithErrorCode("WorkItems.TitleRequired")
            .MaximumLength(WorkItem.TitleMaxLength).WithErrorCode("WorkItems.TitleTooLong");
    }
}
