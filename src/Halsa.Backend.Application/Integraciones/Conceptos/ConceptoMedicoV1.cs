using FluentValidation;
using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Application.Integraciones.Contratos;

namespace Halsa.Backend.Application.Integraciones.Conceptos;

/// <summary>
/// <c>ConceptoMedicoRequest</c> del contrato, versión 1.0: un concepto médico, una evolución o un concepto de
/// rehabilitación que la IPS envía desde su sistema. A diferencia del backoffice, el profesional viaja en el
/// payload: la IPS responde por su personal, y su service account queda en <c>profesional_sub</c> y en la auditoría.
/// </summary>
/// <param name="CorrigeA">Si viene, el concepto corrige a ese registro del mismo caso.</param>
public sealed record ConceptoMedicoV1(
    string? Version,
    Guid? CasoId,
    string? Tipo,
    DateTimeOffset? AtendidoEn,
    Guid? CorrigeA,
    ProfesionalV1? Profesional,
    ContenidoClinicoRequest? Contenido);

/// <summary>
/// El validador de <see cref="RecibirConceptoMedicoCommand"/>: el formato del cuerpo, con los errores por ruta JSON
/// del contrato. Las reglas del contenido son las mismas del backoffice; las del caso las aplica el handler.
/// </summary>
internal sealed class ConceptoMedicoV1Validator : AbstractValidator<ConceptoMedicoV1>
{
    public ConceptoMedicoV1Validator(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        RuleFor(x => x.Version)
            .NotEmpty().WithMessage(MensajesV1.Requerido)
            .Equal(ValoresV1.Version).WithMessage($"Debe ser '{ValoresV1.Version}'.");

        RuleFor(x => x.CasoId)
            .Presente()
            .NotEqual(Guid.Empty).WithMessage(MensajesV1.Formato);

        RuleFor(x => x.Tipo)
            .Must(tipo => TiposConcepto.TryParse(tipo, out _))
            .WithMessage($"El tipo debe ser uno de: {string.Join(", ", TiposConcepto.Codigos)}.");

        RuleFor(x => x.AtendidoEn)
            .Presente()
            .Must(atendidoEn => atendidoEn is null || atendidoEn <= time.GetUtcNow())
            .WithMessage("El momento de la atención no puede ser futuro.");

        RuleFor(x => x.CorrigeA)
            .NotEqual(Guid.Empty).WithMessage(MensajesV1.Formato);

        // RegistroRethus is required here: the IPS signs for a doctor, never for an informant.
        RuleFor(x => x.Profesional).Presente().SetValidator(new ProfesionalV1Validator()!);

        ContenidoClinicoRequestValidator.Contenido(RuleFor(x => x.Contenido));
    }
}
