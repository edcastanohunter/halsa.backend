using Halsa.Backend.Domain.Casos;

namespace Halsa.Backend.Application.Casos.Queries.GetCasoById;

/// <summary>Vista completa de un caso médico para un GET by ID.</summary>
public sealed record CasoDetalle(
    Guid Id,
    string Radicado,
    string TenantId,
    TipoEvento TipoEvento,
    EstadoCaso Estado,
    DateTimeOffset FechaEvento,
    DateTimeOffset RadicadoEn,
    bool EsGraveOMortal,
    string MunicipioDivipola,
    IdentificacionDto Trabajador,
    IdentificacionDto Empleador,
    CalificacionDto? Calificacion,
    IReadOnlyList<RemisionDto> Remisiones,
    IReadOnlyList<AsignacionDto> Asignaciones);

public sealed record IdentificacionDto(string Tipo, string Numero);

public sealed record CalificacionDto(
    decimal? PorcentajePcl,
    DateOnly? FechaEstructuracion,
    string Origen);

public sealed record RemisionDto(
    Guid Id,
    string IpsId,
    string RemitidoPor,
    DateTimeOffset RemitidoEn,
    bool Vigente,
    DateTimeOffset? RevocadaEn,
    string? RevocadaPor);

public sealed record AsignacionDto(
    string Sub,
    string AsignadoPor,
    DateTimeOffset AsignadoEn);
