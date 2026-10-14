using System.Text.RegularExpressions;
using FluentValidation;
using Halsa.Backend.Application.Integraciones.Contratos;
using Halsa.Backend.Domain.Incapacidades;

namespace Halsa.Backend.Application.Incapacidades;

/// <summary>
/// El formato de la parte <c>datos</c> (SPEC 07). <c>profesional</c> se valida si viene; que sea obligatorio en la
/// integración lo decide el handler, que conoce el canal. Las reglas contra el caso son del dominio.
/// </summary>
internal sealed partial class IncapacidadRequestValidator : AbstractValidator<IncapacidadRequest>
{
    public const int MaxDiagnosticosRelacionados = 3;

    public const int MaxObservaciones = 1000;

    private static readonly TimeSpan ZonaColombia = TimeSpan.FromHours(-5);

    public IncapacidadRequestValidator(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        RuleFor(x => x.NumeroCertificado).Requerido(40);

        RuleFor(x => x.Tipo)
            .NotEmpty().WithMessage(MensajesV1.Requerido)
            .Must(tipo => CodigosIncapacidad.TryParse<TipoIncapacidad>(tipo, out _))
            .WithMessage($"Debe ser uno de: {string.Join(", ", CodigosIncapacidad.Tipos)}.");

        // A PRORROGA without prorrogaDe is a business rule (422 PRORROGA_SIN_INICIAL), not a format error.
        RuleFor(x => x.ProrrogaDe).NotEqual(Guid.Empty).WithMessage(MensajesV1.Formato);
        RuleFor(x => x.ProrrogaDe)
            .Null().When(x => x.Tipo == CodigosIncapacidad.Inicial)
            .WithMessage("Una incapacidad INICIAL no prorroga a otra.");

        // fecha_inicio is the clinical record's care date, which cannot be in the future.
        RuleFor(x => x.FechaInicio)
            .Presente()
            .Must(fecha => fecha is null || fecha <= Hoy(time))
            .WithMessage("La fecha de inicio no puede ser futura.");

        RuleFor(x => x.Dias)
            .Presente()
            .InclusiveBetween(1, Incapacidad.MaxDias).WithMessage($"Debe estar entre 1 y {Incapacidad.MaxDias}.");

        RuleFor(x => x.DiagnosticoPrincipal).Presente().SetValidator(new DiagnosticoCie10Validator()!);

        RuleFor(x => x.DiagnosticosRelacionados)
            .Must(lista => lista is null || lista.Count <= MaxDiagnosticosRelacionados)
            .WithMessage($"Admite como máximo {MaxDiagnosticosRelacionados} diagnósticos.");
        RuleForEach(x => x.DiagnosticosRelacionados).SetValidator(new DiagnosticoCie10Validator());

        RuleFor(x => x.Profesional).SetValidator(new ProfesionalV1Validator()!);

        RuleFor(x => x.IpsEmisora).Presente().SetValidator(new IpsEmisoraValidator()!);

        RuleFor(x => x.Modalidad)
            .NotEmpty().WithMessage(MensajesV1.Requerido)
            .Must(modalidad => CodigosIncapacidad.TryParse<ModalidadIncapacidad>(modalidad, out _))
            .WithMessage($"Debe ser uno de: {string.Join(", ", CodigosIncapacidad.Modalidades)}.");

        RuleFor(x => x.Observaciones).Opcional(MaxObservaciones);
    }

    private static DateOnly Hoy(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().ToOffset(ZonaColombia).DateTime);

    private sealed class IpsEmisoraValidator : AbstractValidator<IpsEmisoraV1>
    {
        public IpsEmisoraValidator()
        {
            RuleFor(i => i.CodigoReps)
                .NotEmpty().WithMessage(MensajesV1.Requerido)
                .Matches("^[0-9]{10,12}$").WithMessage(MensajesV1.Formato);

            RuleFor(i => i.Nombre).Opcional(200);
        }
    }

    /// <summary>Un <c>Codificado</c> con <c>sistema=CIE-10</c> y el formato de la SPEC 06 (<c>S62</c>, <c>S62.6</c>).</summary>
    private sealed class DiagnosticoCie10Validator : AbstractValidator<CodificadoV1>
    {
        public DiagnosticoCie10Validator()
        {
            RuleFor(d => d.Sistema)
                .NotEmpty().WithMessage(MensajesV1.Requerido)
                .Equal(ValoresV1.SistemaCie10).WithMessage($"Debe ser '{ValoresV1.SistemaCie10}'.");

            RuleFor(d => d.Codigo)
                .NotEmpty().WithMessage(MensajesV1.Requerido)
                .Matches(Cie10()).WithMessage("El código CIE-10 no tiene un formato válido (por ejemplo, S62 o S62.6).");

            RuleFor(d => d.Descripcion).Opcional(250);
        }
    }

    [GeneratedRegex(@"^[A-Z][0-9]{2}(\.[0-9A-Z]{1,4})?$", RegexOptions.CultureInvariant)]
    private static partial Regex Cie10();
}
