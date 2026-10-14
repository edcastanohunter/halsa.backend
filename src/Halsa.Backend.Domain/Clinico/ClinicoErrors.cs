using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Domain.Clinico;

/// <summary>Errores del contexto clínico. Todos tienen código <c>Clinico.*</c>.</summary>
public static class ClinicoErrors
{
    public static readonly Error CasoNoEncontrado =
        Error.NotFound("Clinico.CasoNoEncontrado",
            "El caso no existe o no es visible para el actor actual.");

    public static readonly Error RegistroNoEncontrado =
        Error.NotFound("Clinico.RegistroNoEncontrado",
            "El registro clínico no existe o no es visible para el actor actual.");

    public static readonly Error AtendidoEnFuturo =
        Error.Problem("Clinico.AtendidoEnFuturo",
            "El momento de la atención no puede ser futuro.");

    public static readonly Error RethusRequerido =
        Error.Problem("Clinico.RethusRequerido",
            "El registro RETHUS del profesional es obligatorio salvo en el reporte inicial.");

    public static readonly Error ProfesionalNoIdentificado =
        Error.Unauthorized("Clinico.ProfesionalNoIdentificado",
            "No se pudo identificar al profesional que firma el registro.");

    public static readonly Error ProfesionalSinRegistro =
        Error.Forbidden("Clinico.ProfesionalSinRegistro",
            "El token no trae la identidad del profesional (rethus, tipo_documento y numero_documento).");

    public static readonly Error FechaAtencionAnteriorAlEvento =
        Error.Problem("Clinico.FechaAtencionAnteriorAlEvento",
            "El momento de la atención es anterior a la fecha del evento del caso.");

    public static readonly Error CasoNoAdmiteRegistros =
        Error.Conflict("Clinico.CasoNoAdmiteRegistros",
            "El caso está rechazado o cerrado y no admite registros clínicos nuevos.");

    public static readonly Error ActorNoAutorizado =
        Error.Forbidden("Clinico.ActorNoAutorizado",
            "El tipo de actor no puede registrar conceptos médicos por este canal.");

    public static readonly Error NoAsignado =
        Error.Forbidden("Clinico.NoAsignado",
            "El caso es visible, pero el profesional no está asignado a él.");

    public static readonly Error AccesoNoPermitido =
        Error.Forbidden("Clinico.AccesoNoPermitido",
            "El tipo de actor nunca accede a la historia clínica.");
}
