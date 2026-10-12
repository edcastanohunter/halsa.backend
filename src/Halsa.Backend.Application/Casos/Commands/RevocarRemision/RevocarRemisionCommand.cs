using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Casos;

namespace Halsa.Backend.Application.Casos.Commands.RevocarRemision;

/// <summary>Revoca una remisión vigente del caso indicado.</summary>
public sealed record RevocarRemisionCommand(
    CasoMedicoId CasoId,
    RemisionId RemisionId) : ICommand;
