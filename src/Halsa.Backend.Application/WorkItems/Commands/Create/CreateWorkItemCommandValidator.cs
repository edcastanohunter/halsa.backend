using Halsa.Backend.Domain.WorkItems;
using FluentValidation;

namespace Halsa.Backend.Application.WorkItems.Commands.Create;

internal sealed class CreateWorkItemCommandValidator : AbstractValidator<CreateWorkItemCommand>
{
    public CreateWorkItemCommandValidator()
    {
        RuleFor(command => command.Title)
            .NotEmpty().WithErrorCode("WorkItems.TitleRequired")
            .MaximumLength(WorkItem.TitleMaxLength).WithErrorCode("WorkItems.TitleTooLong");

        RuleFor(command => command.Description)
            .MaximumLength(2_000).WithErrorCode("WorkItems.DescriptionTooLong");
    }
}
