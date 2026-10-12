using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Domain.Casos;

/// <summary>
/// Errores de dominio de <see cref="CasoMedico"/>. Todos tienen código <c>Caso.*</c>.
/// </summary>
public static class CasoMedicoErrors
{
    public static readonly Error FechaEventoFutura =
        Error.Problem("Caso.FechaEventoFutura",
            "La fecha del evento no puede ser futura.");

    public static readonly Error RemisionDuplicada =
        Error.Conflict("Caso.RemisionDuplicada",
            "Ya existe una remisión vigente a esta IPS para este caso.");

    public static readonly Error RemisionNoEncontrada =
        Error.NotFound("Caso.RemisionNoEncontrada",
            "La remisión indicada no existe en este caso.");

    public static readonly Error RemisionYaRevocada =
        Error.Conflict("Caso.RemisionYaRevocada",
            "La remisión ya ha sido revocada.");

    public static readonly Error AsignacionNoEncontrada =
        Error.NotFound("Caso.AsignacionNoEncontrada",
            "El sub indicado no está asignado a este caso.");

    public static readonly Error ActorNoAutorizado =
        Error.Forbidden("Caso.ActorNoAutorizado",
            "Solo un actor de tipo ARL puede realizar esta operación sobre un caso.");

    public static readonly Error ConflictoConcurrencia =
        Error.Conflict("Caso.ConflictoConcurrencia",
            "Otro proceso modificó el caso al mismo tiempo. Reintenta la operación.");

    public static readonly Error IpsIdInvalido =
        Error.Problem("Caso.IpsIdInvalido",
            "El código REPS de la IPS debe tener 12 dígitos.");

    public static readonly Error NoEncontrado =
        Error.NotFound("Caso.NoEncontrado",
            "El caso no existe o no pertenece al contexto de tenant actual.");
}
