using FluentValidation;

namespace Halsa.Backend.Application.Casos.Commands.Remitir;

internal sealed class RemitirCasoCommandValidator : AbstractValidator<RemitirCasoCommand>
{
    public RemitirCasoCommandValidator()
    {
        RuleFor(command => command.IpsId)
            .NotEmpty().WithErrorCode("Caso.IpsIdRequerido")
            .Matches("^[0-9]{12}$").WithErrorCode("Caso.IpsIdInvalido");
    }
}
