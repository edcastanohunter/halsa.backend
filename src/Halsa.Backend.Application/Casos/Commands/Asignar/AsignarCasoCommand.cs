using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Casos;

namespace Halsa.Backend.Application.Casos.Commands.Asignar;

/// <summary>Asigna un usuario (sub) al caso. Idempotente.</summary>
public sealed record AsignarCasoCommand(
    CasoMedicoId CasoId,
    string Sub) : ICommand;
