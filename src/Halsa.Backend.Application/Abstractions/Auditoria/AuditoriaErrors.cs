using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Abstractions.Auditoria;

/// <summary>Errores de auditoría. Todos tienen código <c>Auditoria.*</c>.</summary>
public static class AuditoriaErrors
{
    /// <summary>La lectura no se pudo registrar, así que sus datos no se entregan (falla cerrado).</summary>
    public static readonly Error NoRegistrada =
        Error.Failure("Auditoria.NoRegistrada",
            "No se pudo registrar el acceso en la auditoría; la respuesta se descartó.");

    public static readonly Error ActorNoAutorizado =
        Error.Forbidden("Auditoria.ActorNoAutorizado",
            "Solo un actor de tipo ARL puede consultar la auditoría de accesos.");
}
