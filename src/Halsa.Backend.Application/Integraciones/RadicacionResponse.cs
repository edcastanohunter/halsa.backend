namespace Halsa.Backend.Application.Integraciones;

/// <summary><c>RadicacionResponse</c> del contrato: la respuesta de la radicación y de la consulta de estado.</summary>
/// <param name="Estado"><c>RADICADO</c>, <c>VALIDADO</c>, <c>RECHAZADO</c> o <c>DUPLICADO</c>.</param>
/// <param name="Plazos">Los plazos legales que abrió la radicación. Vacío hasta la SPEC 09.</param>
/// <param name="MotivoRechazo">Solo con <c>RECHAZADO</c>.</param>
public sealed record RadicacionResponse(
    Guid SolicitudId,
    Guid CasoId,
    string Radicado,
    string Estado,
    DateTimeOffset RecibidoEn,
    IReadOnlyList<PlazoResumen> Plazos,
    Problema? MotivoRechazo = null);

/// <summary><c>PlazoResumen</c> del contrato. Lo llena la SPEC 09.</summary>
public sealed record PlazoResumen(string Regla, string? BaseLegal, DateTimeOffset VenceEn);

/// <summary><c>Problema</c> del contrato (RFC 9457), para el <c>motivoRechazo</c>.</summary>
public sealed record Problema(string Type, string Title, int Status, string Code, string? Detail);

/// <summary>Los valores de <see cref="RadicacionResponse.Estado"/>.</summary>
public static class EstadosRadicacion
{
    public const string Radicado = "RADICADO";
    public const string Validado = "VALIDADO";
    public const string Rechazado = "RECHAZADO";
    public const string Duplicado = "DUPLICADO";
}
