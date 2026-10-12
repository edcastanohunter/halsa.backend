using Halsa.Backend.Domain.Casos;

namespace Halsa.Backend.Application.Casos.Queries.ListCasos;

/// <summary>Projection ligera usada en la lista paginada de casos.</summary>
public sealed record CasoResumen(
    Guid Id,
    string Radicado,
    string TenantId,
    TipoEvento TipoEvento,
    EstadoCaso Estado,
    DateTimeOffset FechaEvento,
    DateTimeOffset RadicadoEn,
    bool EsGraveOMortal,
    string TrabajadorTipo,
    string TrabajadorNumero);
