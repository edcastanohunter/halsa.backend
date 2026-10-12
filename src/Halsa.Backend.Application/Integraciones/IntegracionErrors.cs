using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Integraciones;

/// <summary>Errores de la ingesta (SPEC 05). Todos tienen código <c>Integracion.*</c>.</summary>
public static class IntegracionErrors
{
    public static readonly Error ActorNoAutorizado =
        Error.Forbidden("Integracion.ActorNoAutorizado",
            "Solo la integración de una ARL (actor_tipo=integracion con tenant_id) puede radicar o consultar reportes.");

    public static readonly Error IdempotencyKeyInvalida =
        Error.Problem("Integracion.IdempotencyKeyInvalida",
            "La cabecera Idempotency-Key es obligatoria y debe ser un UUID.");

    public static readonly Error IdempotenciaConflicto =
        Error.Conflict("Integracion.IdempotenciaConflicto",
            "La Idempotency-Key ya se usó con un cuerpo distinto.");

    public static readonly Error SolicitudNoEncontrada =
        Error.NotFound("Integracion.SolicitudNoEncontrada",
            "La solicitud no existe o no pertenece al tenant del actor.");

    /// <summary>El código de <see cref="ReporteYaRadicadoError"/>.</summary>
    public const string ReporteYaRadicadoCodigo = "Integracion.ReporteYaRadicado";

    /// <summary>El código de <see cref="ErrorDeCampo"/>.</summary>
    public const string CampoInvalidoCodigo = "Integracion.CampoInvalido";

    /// <summary>El cuerpo no es un objeto JSON, o un campo no tiene el tipo que exige el contrato.</summary>
    public static ValidationError Validacion(IEnumerable<ErrorDeCampo> errores) => new([.. errores]);
}

/// <summary>
/// Otra <c>Idempotency-Key</c> ya radicó el mismo <c>numeroReporteOrigen</c>. Lleva el radicado existente,
/// para que el core de la ARL pueda conciliar sin otra consulta.
/// </summary>
public sealed record ReporteYaRadicadoError : Error
{
    public ReporteYaRadicadoError(string radicado)
        : base(IntegracionErrors.ReporteYaRadicadoCodigo,
            "El reporte ya se radicó con otra Idempotency-Key.",
            ErrorType.Conflict)
    {
        Radicado = radicado;
    }

    public string Radicado { get; }
}

/// <summary>Un error de validación de un campo, con su ruta JSON (<c>evento.fechaHora</c>) como en el contrato.</summary>
public sealed record ErrorDeCampo : Error
{
    public ErrorDeCampo(string ruta, string mensaje)
        : base(IntegracionErrors.CampoInvalidoCodigo, mensaje, ErrorType.Problem)
    {
        Ruta = ruta;
    }

    public string Ruta { get; }
}
