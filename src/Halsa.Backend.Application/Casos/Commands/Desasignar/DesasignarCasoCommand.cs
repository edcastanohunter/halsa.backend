using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Casos;

namespace Halsa.Backend.Application.Casos.Commands.Desasignar;

/// <summary>Elimina la asignación de un usuario (sub) del caso.</summary>
public sealed record DesasignarCasoCommand(
    CasoMedicoId CasoId,
    string Sub) : ICommand;
