using FluentValidation;
using Halsa.Backend.Domain.Clinico;

namespace Halsa.Backend.Application.Clinico;

/// <summary>Reglas comunes a registrar y corregir un registro clínico.</summary>
internal abstract class RegistroClinicoValidator<TCommand> : AbstractValidator<TCommand>
    where TCommand : IRegistroClinicoInput
{
    protected RegistroClinicoValidator()
    {
        RuleFor(command => command.CasoId.Value)
            .NotEmpty().WithErrorCode("Clinico.CasoIdRequerido");

        RuleFor(command => command.Tipo)
            .IsInEnum().WithErrorCode("Clinico.TipoInvalido");

        RuleFor(command => command.AtendidoEn)
            .NotEmpty().WithErrorCode("Clinico.AtendidoEnRequerido");

        RuleFor(command => command.Contenido)
            .NotNull().WithErrorCode("Clinico.ContenidoRequerido")
            .Must(TieneTexto).WithErrorCode("Clinico.ContenidoVacio")
            .WithMessage("Al menos un campo del contenido clínico debe tener texto.");

        RuleForEach(command => command.Contenido.Diagnosticos)
            .ChildRules(diagnostico =>
            {
                diagnostico.RuleFor(d => d.Sistema).NotEmpty().WithErrorCode("Clinico.DiagnosticoSistemaRequerido");
                diagnostico.RuleFor(d => d.Codigo).NotEmpty().WithErrorCode("Clinico.DiagnosticoCodigoRequerido");
            })
            .When(command => command.Contenido?.Diagnosticos is not null);

        RuleFor(command => command.Profesional)
            .NotNull().WithErrorCode("Clinico.ProfesionalRequerido");

        RuleFor(command => command.Profesional.TipoDocumento)
            .NotEmpty().WithErrorCode("Clinico.ProfesionalTipoDocumentoRequerido")
            .When(command => command.Profesional is not null);

        RuleFor(command => command.Profesional.NumeroDocumento)
            .NotEmpty().WithErrorCode("Clinico.ProfesionalNumeroDocumentoRequerido")
            .When(command => command.Profesional is not null);

        // Only the FURAT informant (ReporteInicial) may lack RETHUS; the domain enforces it too.
        RuleFor(command => command.Profesional.RegistroRethus)
            .NotEmpty().WithErrorCode("Clinico.RethusRequerido")
            .When(command => command.Profesional is not null && command.Tipo != TipoRegistroClinico.ReporteInicial);
    }

    private static bool TieneTexto(ContenidoClinico? contenido) =>
        contenido is not null &&
        new[]
        {
            contenido.Motivo, contenido.Anamnesis, contenido.ExamenFisico,
            contenido.Analisis, contenido.Plan, contenido.Observaciones
        }.Any(campo => !string.IsNullOrWhiteSpace(campo));
}
