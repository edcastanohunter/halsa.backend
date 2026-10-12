using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;

namespace Halsa.Backend.Application.Clinico.Commands.Corregir;

/// <summary>
/// Corrige un registro clínico. La historia es append-only: el original no cambia, y se añade un
/// registro nuevo que lo referencia con <c>CorrigeA</c>. Devuelve el id del registro nuevo.
/// </summary>
public sealed record CorregirRegistroClinicoCommand(
    HistoriaClinicaDetalleId RegistroId,
    CasoMedicoId CasoId,
    TipoRegistroClinico Tipo,
    DateTimeOffset AtendidoEn,
    ContenidoClinico Contenido,
    DatosProfesional Profesional) : ICommand<Guid>, IRegistroClinicoInput;
