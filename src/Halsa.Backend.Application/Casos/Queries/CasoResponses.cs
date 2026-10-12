using Halsa.Backend.Domain.Casos;

namespace Halsa.Backend.Application.Casos.Queries;

/// <summary>Fila de la lista paginada de casos. Proyección explícita: nunca se expone la entidad.</summary>
public sealed record CasoResumenResponse(
    Guid Id,
    string Radicado,
    string TipoEvento,
    string Estado,
    DateTimeOffset FechaEvento,
    string TrabajadorDocumento,
    bool TieneCalificacion,
    decimal? PorcentajePcl);

/// <summary>Detalle de un caso, con sus remisiones vigentes y su equipo asignado.</summary>
public sealed record CasoDetalleResponse(
    CasoResumenResponse Resumen,
    string EmpleadorDocumento,
    string MunicipioDivipola,
    bool EsGraveOMortal,
    IReadOnlyList<RemisionResponse> RemisionesVigentes,
    IReadOnlyList<string> EquipoAsignado);

public sealed record RemisionResponse(Guid Id, string IpsId, DateTimeOffset RemitidoEn);

internal static class CasoResponseMapping
{
    public static string Documento(Identificacion identificacion) =>
        $"{identificacion.Tipo} {identificacion.Numero}";

    public static CasoResumenResponse ToResumen(this CasoMedico caso) => new(
        caso.Id.Value,
        caso.Radicado,
        caso.TipoEvento.ToString(),
        caso.Estado.ToString(),
        caso.FechaEvento,
        Documento(caso.Trabajador),
        caso.Calificacion is not null,
        caso.Calificacion?.PorcentajePcl);
}
