using FluentValidation;

namespace Halsa.Backend.Application.Casos.Commands.Asignar;

internal sealed class AsignarCasoCommandValidator : AbstractValidator<AsignarCasoCommand>
{
    public AsignarCasoCommandValidator()
    {
        RuleFor(command => command.Sub)
            .NotEmpty().WithErrorCode("Caso.SubRequerido")
            .MaximumLength(64).WithErrorCode("Caso.SubDemasiadoLargo");
    }
}
