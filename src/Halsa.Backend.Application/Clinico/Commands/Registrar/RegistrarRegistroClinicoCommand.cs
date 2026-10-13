using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;

namespace Halsa.Backend.Application.Clinico.Commands.Registrar;

/// <summary>Añade un registro a la historia clínica del caso. Devuelve el id del registro y cuándo se guardó.</summary>
public sealed record RegistrarRegistroClinicoCommand(
    CasoMedicoId CasoId,
    TipoRegistroClinico Tipo,
    DateTimeOffset AtendidoEn,
    ContenidoClinico Contenido,
    DatosProfesional Profesional) : ICommand<RegistroClinicoCreadoResponse>, IRegistroClinicoInput;
