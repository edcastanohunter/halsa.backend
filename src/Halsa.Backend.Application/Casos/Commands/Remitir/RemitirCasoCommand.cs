using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Casos;

namespace Halsa.Backend.Application.Casos.Commands.Remitir;

/// <summary>Crea una remisión del caso indicado a la IPS indicada.</summary>
public sealed record RemitirCasoCommand(
    CasoMedicoId CasoId,
    string IpsId) : ICommand<Guid>;
