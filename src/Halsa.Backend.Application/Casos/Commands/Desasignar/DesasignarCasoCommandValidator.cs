using FluentValidation;

namespace Halsa.Backend.Application.Casos.Commands.Desasignar;

internal sealed class DesasignarCasoCommandValidator : AbstractValidator<DesasignarCasoCommand>
{
    public DesasignarCasoCommandValidator()
    {
        RuleFor(command => command.Sub)
            .NotEmpty().WithErrorCode("Caso.SubRequerido");
    }
}
