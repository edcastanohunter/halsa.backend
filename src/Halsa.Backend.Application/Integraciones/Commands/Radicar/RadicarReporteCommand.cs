using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Integraciones.Canonico;

namespace Halsa.Backend.Application.Integraciones.Commands.Radicar;

/// <summary>
/// Radica un FURAT o FUREP recibido del core de una ARL. Lleva el cuerpo crudo: su SHA-256 es la huella de la
/// idempotencia y el propio cuerpo, cifrado, la evidencia de lo que se recibió.
/// </summary>
/// <param name="IdempotencyKey">La cabecera tal cual llegó: el handler comprueba que sea un UUID después del actor.</param>
/// <param name="ClientId">El cliente OAuth del emisor (<c>azp</c>).</param>
public sealed record RadicarReporteCommand(
    TipoReporte Tipo,
    string? IdempotencyKey,
    ReadOnlyMemory<byte> Cuerpo,
    string? ClientId,
    string CorrelationId) : ICommand<RadicacionResponse>;
