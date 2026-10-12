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
}
