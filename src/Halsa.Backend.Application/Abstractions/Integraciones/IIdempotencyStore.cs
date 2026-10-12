using Halsa.Backend.Application.Integraciones.Registros;

namespace Halsa.Backend.Application.Abstractions.Integraciones;

/// <summary>
/// La idempotencia por <c>(emisor, Idempotency-Key)</c> (<c>integracion.solicitudes_idempotentes</c>). Una
/// llave vencida se ignora al consultar y se puede reclamar de nuevo; la purga es de la SPEC 09.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>La respuesta guardada para la llave, si sigue vigente en <paramref name="ahora"/>.</summary>
    Task<RespuestaIdempotente?> BuscarVigenteAsync(string emisor, Guid llave, DateTimeOffset ahora, CancellationToken ct);

    /// <summary>
    /// Reclama la llave dentro de la transacción abierta de la ingesta. Devuelve <c>false</c> si otra solicitud
    /// la tiene vigente: perdió la carrera, y quien llama revierte y relee la ganadora.
    /// </summary>
    Task<bool> ReclamarAsync(SolicitudIdempotente solicitud, CancellationToken ct);
}

/// <summary>Lo guardado para una llave: el hash del cuerpo con que se usó y la respuesta que se dio.</summary>
public sealed record RespuestaIdempotente(byte[] CuerpoSha256, Guid SolicitudId, string Respuesta);
