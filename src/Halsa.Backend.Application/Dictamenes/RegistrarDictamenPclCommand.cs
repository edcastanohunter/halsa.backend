using FluentValidation;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Domain.Dictamenes;

namespace Halsa.Backend.Application.Dictamenes;

/// <summary>
/// Registra el dictamen PCL del caso, o corrige el vigente con <c>corrigeA</c> (SPEC 08).
/// </summary>
/// <param name="Calificador">El profesional del token (<c>IProfesionalActual</c>).</param>
/// <param name="CalificadorNombre">El claim <c>name</c> del token, si lo trae.</param>
public sealed record RegistrarDictamenPclCommand(
    CasoMedicoId CasoId,
    RegistrarDictamenPclRequest Request,
    ProfesionalRef Calificador,
    string? CalificadorNombre) : ICommand<DictamenPclResponse>;

/// <summary>Las reglas de formato del dictamen. Las del estado (ya existe, ya notificado) son del handler.</summary>
internal sealed class RegistrarDictamenPclCommandValidator : AbstractValidator<RegistrarDictamenPclCommand>
{
    public const int MinimoFundamentos = 50;

    public const int MaximoFundamentos = 20_000;

    /// <summary>La tolerancia de la suma de los componentes.</summary>
    public const decimal ToleranciaComponentes = 0.01m;

    private static readonly TimeSpan ZonaColombia = TimeSpan.FromHours(-5);

    public RegistrarDictamenPclCommandValidator(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        RuleFor(c => c.Request).NotNull().ChildRules(request =>
        {
            request.RuleFor(r => r.PorcentajePcl)
                .NotNull().WithErrorCode("Dictamen.PorcentajeRequerido").WithMessage("Es obligatorio.")
                .Must(p => p is not decimal v || (v is >= 0 and <= 100 && decimal.Round(v, 2) == v))
                .WithErrorCode(DictamenErrors.PorcentajeFueraDeRango.Code)
                .WithMessage(DictamenErrors.PorcentajeFueraDeRango.Description);

            request.RuleFor(r => r.Origen)
                .Must(o => CodigosDictamen.TryParse<OrigenPcl>(o, out _)).WithErrorCode("Dictamen.OrigenInvalido")
                .WithMessage("Debe ser LABORAL, COMUN o MIXTO.");

            request.RuleFor(r => r.FechaEstructuracion)
                .NotNull().WithErrorCode("Dictamen.FechaEstructuracionRequerida").WithMessage("Es obligatoria.");

            request.RuleFor(r => r.FechaDictamen)
                .NotNull().WithErrorCode("Dictamen.FechaDictamenRequerida").WithMessage("Es obligatoria.");

            request.RuleFor(r => r)
                .Must(r => r.FechaEstructuracion is not DateOnly e || r.FechaDictamen is not DateOnly d ||
                           (e <= d && d <= Hoy(time)))
                .WithName("fechaDictamen")
                .WithErrorCode(DictamenErrors.FechasInvalidas.Code)
                .WithMessage(DictamenErrors.FechasInvalidas.Description);

            request.RuleFor(r => r.Componentes)
                .Must(c => c is null || c.Deficiencias is >= 0 && c.RolLaboralYOtrasAreas is >= 0)
                .WithErrorCode("Dictamen.ComponentesInvalidos")
                .WithMessage("deficiencias y rolLaboralYOtrasAreas son obligatorios y no negativos.");

            request.RuleFor(r => r)
                .Must(SumanElPorcentaje)
                .WithName("componentes")
                .WithErrorCode("Dictamen.ComponentesNoSuman")
                .WithMessage("deficiencias + rolLaboralYOtrasAreas debe ser igual al porcentaje de PCL (±0,01).");

            request.RuleFor(r => r.Fundamentos)
                .NotEmpty().WithErrorCode("Dictamen.FundamentosRequeridos").WithMessage("Es obligatorio.")
                .Length(MinimoFundamentos, MaximoFundamentos).WithErrorCode("Dictamen.FundamentosLongitud")
                .WithMessage("Debe tener entre {MinLength} y {MaxLength} caracteres.");

            request.RuleFor(r => r.Diagnosticos)
                .NotEmpty().WithErrorCode("Dictamen.DiagnosticoRequerido")
                .WithMessage("Se requiere al menos un diagnóstico CIE-10.");

            request.RuleForEach(r => r.Diagnosticos).SetValidator(new DiagnosticoRequestValidator());
        });
    }

    private static DateOnly Hoy(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().ToOffset(ZonaColombia).DateTime);

    private static bool SumanElPorcentaje(RegistrarDictamenPclRequest request) =>
        request.Componentes is not { Deficiencias: decimal d, RolLaboralYOtrasAreas: decimal r } ||
        request.PorcentajePcl is not decimal p ||
        Math.Abs(d + r - p) <= ToleranciaComponentes;
}
