using FluentValidation;

namespace Halsa.Backend.Application.Clinico.Commands.Corregir;

internal sealed class CorregirRegistroClinicoCommandValidator
    : RegistroClinicoValidator<CorregirRegistroClinicoCommand>
{
    public CorregirRegistroClinicoCommandValidator()
    {
        RuleFor(command => command.RegistroId.Value)
            .NotEmpty().WithErrorCode("Clinico.RegistroIdRequerido");
    }
}
