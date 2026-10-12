using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Casos;

namespace Halsa.Backend.Application.Casos.Commands.Radicar;

/// <summary>
/// Registra (radica) un nuevo caso de salud laboral para el tenant del actor autenticado.
/// </summary>
public sealed record RadicarCasoCommand(
    string Radicado,
    TipoEvento TipoEvento,
    string TrabajadorTipo,
    string TrabajadorNumero,
    string EmpleadorTipo,
    string EmpleadorNumero,
    DateTimeOffset FechaEvento,
    string MunicipioDivipola,
    bool EsGraveOMortal) : ICommand<Guid>;
