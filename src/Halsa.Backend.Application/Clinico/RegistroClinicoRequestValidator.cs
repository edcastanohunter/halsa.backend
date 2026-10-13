using System.Text.RegularExpressions;
using FluentValidation;

namespace Halsa.Backend.Application.Clinico;

/// <summary>
/// Las reglas de formato del contenido de un concepto (SPEC 06), comunes a la integración y al backoffice. Solo el
/// formato del CIE-10: validarlo contra el catálogo es otra spec.
/// </summary>
internal sealed partial class ContenidoClinicoRequestValidator : AbstractValidator<ContenidoClinicoRequest>
{
    public const int MaximoCaracteres = 8000;

    public const string SistemaCie10 = "CIE-10";

    public ContenidoClinicoRequestValidator()
    {
        RuleFor(x => x.Motivo).Texto();
        RuleFor(x => x.Anamnesis).Texto();
        RuleFor(x => x.ExamenFisico).Texto();
        RuleFor(x => x.Analisis).Texto();
        RuleFor(x => x.Plan).Texto();
        RuleFor(x => x.Observaciones).Texto();

        RuleForEach(x => x.Diagnosticos).ChildRules(diagnostico =>
        {
            diagnostico.RuleFor(d => d.Sistema)
                .NotEmpty().WithErrorCode("Clinico.DiagnosticoSistemaInvalido").WithMessage("Es obligatorio.")
                .Equal(SistemaCie10).WithErrorCode("Clinico.DiagnosticoSistemaInvalido")
                .WithMessage($"Debe ser '{SistemaCie10}'.");

            diagnostico.RuleFor(d => d.Codigo)
                .NotEmpty().WithErrorCode("Clinico.DiagnosticoCodigoInvalido").WithMessage("Es obligatorio.")
                .Matches(Cie10()).WithErrorCode("Clinico.DiagnosticoCodigoInvalido")
                .WithMessage("El código CIE-10 no tiene un formato válido (por ejemplo, S62 o S62.6).");

            diagnostico.RuleFor(d => d.Descripcion)
                .MaximumLength(250).WithErrorCode("Clinico.CampoDemasiadoLargo")
                .WithMessage("Admite como máximo {MaxLength} caracteres.");
        });
    }

    /// <summary>Una letra, dos dígitos y, opcionalmente, un punto y de 1 a 4 caracteres (<c>S62</c>, <c>S62.6</c>).</summary>
    [GeneratedRegex(@"^[A-Z][0-9]{2}(\.[0-9A-Z]{1,4})?$", RegexOptions.CultureInvariant)]
    private static partial Regex Cie10();

    private static bool TieneTexto(ContenidoClinicoRequest? contenido) =>
        contenido is null ||
        new[] { contenido.Motivo, contenido.Anamnesis, contenido.ExamenFisico, contenido.Analisis, contenido.Plan }
            .Any(campo => !string.IsNullOrWhiteSpace(campo));

    /// <summary>
    /// El contenido del concepto, en la regla de su dueño: obligatorio, con texto en al menos uno de los cinco
    /// campos de la nota y con el formato de cada campo.
    /// </summary>
    public static void Contenido<T>(IRuleBuilderInitial<T, ContenidoClinicoRequest?> regla) =>
        regla
            .NotNull().WithErrorCode("Clinico.ContenidoRequerido").WithMessage("Es obligatorio.")
            .Must(TieneTexto).WithErrorCode("Clinico.ContenidoVacio")
            .WithMessage("Al menos uno entre motivo, anamnesis, examenFisico, analisis y plan debe tener texto.")
            .SetValidator(new ContenidoClinicoRequestValidator()!);
}

internal static class ReglasContenido
{
    public static IRuleBuilderOptions<T, string?> Texto<T>(this IRuleBuilder<T, string?> regla) =>
        regla.MaximumLength(ContenidoClinicoRequestValidator.MaximoCaracteres)
            .WithErrorCode("Clinico.CampoDemasiadoLargo")
            .WithMessage("Admite como máximo {MaxLength} caracteres.");
}

/// <summary>El cuerpo del backoffice (SPEC 06). El estado y la fecha del caso los comprueba el comando.</summary>
internal sealed class RegistroClinicoRequestValidator : AbstractValidator<RegistroClinicoRequest>
{
    public RegistroClinicoRequestValidator(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        RuleFor(x => x.Tipo)
            .Must(tipo => TiposConcepto.TryParse(tipo, out _)).WithErrorCode("Clinico.TipoInvalido")
            .WithMessage($"El tipo debe ser uno de: {string.Join(", ", TiposConcepto.Codigos)}.");

        RuleFor(x => x.AtendidoEn)
            .NotNull().WithErrorCode("Clinico.AtendidoEnRequerido").WithMessage("Es obligatorio.")
            .Must(atendidoEn => atendidoEn is null || atendidoEn <= time.GetUtcNow())
            .WithErrorCode("Clinico.AtendidoEnFuturo").WithMessage("El momento de la atención no puede ser futuro.");

        RuleFor(x => x.CorrigeA)
            .NotEqual(Guid.Empty).WithErrorCode("Clinico.RegistroIdRequerido").WithMessage("No puede ser un UUID vacío.");

        ContenidoClinicoRequestValidator.Contenido(RuleFor(x => x.Contenido));
    }
}
