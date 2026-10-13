using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Clinico;

namespace Halsa.Backend.Application.Integraciones.Conceptos;

/// <summary>
/// Registra un concepto médico enviado por la integración de una IPS (SPEC 06). Lleva el cuerpo crudo: su SHA-256 es
/// la huella de la idempotencia, como en la radicación.
/// </summary>
/// <param name="IdempotencyKey">La cabecera tal cual llegó: el handler comprueba que sea un UUID después del actor.</param>
public sealed record RecibirConceptoMedicoCommand(
    string? IdempotencyKey,
    ReadOnlyMemory<byte> Cuerpo) : ICommand<RegistroClinicoCreadoResponse>;
