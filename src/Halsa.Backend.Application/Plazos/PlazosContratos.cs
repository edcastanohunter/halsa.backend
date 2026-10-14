using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Application.Plazos;

// Contratos del outbox del motor de plazos (SPEC 09). Solo tipos primitivos: son contrato público, y el outbox los
// guarda en JSON por su nombre de tipo. Los entrega como webhooks la SPEC 10.

/// <summary>Un plazo abierto llegó a su <c>alerta_en</c>. Lo emite <c>BarridoPlazosJob</c>, una sola vez por plazo.</summary>
public sealed record PlazoProximoAVencerIntegrationEvent(
    Guid PlazoId,
    string TenantId,
    Guid CasoMedicoId,
    string Radicado,
    Guid? ReferenciaId,
    PlazoResumen Plazo) : IntegrationEvent;

/// <summary>
/// Un plazo venció sin cumplirse: lo emite <c>BarridoPlazosJob</c>, o la apertura de un plazo de reporte cuya radicación
/// ya llegó tarde. <see cref="ReferenciaId"/> es la cadena de incapacidades o el dictamen del que cuelga.
/// </summary>
public sealed record PlazoVencidoIntegrationEvent(
    Guid PlazoId,
    string TenantId,
    Guid CasoMedicoId,
    string Radicado,
    Guid? ReferenciaId,
    PlazoResumen Plazo) : IntegrationEvent;

/// <summary><c>PlazoResponse</c> del contrato (<c>GET /v1/casos/{id}/plazos</c> y <c>GET /v1/plazos</c>).</summary>
/// <param name="Estado"><c>ABIERTO</c>, <c>CUMPLIDO</c>, <c>VENCIDO</c> o <c>SUSPENDIDO</c>.</param>
public sealed record PlazoResponse(
    Guid Id,
    Guid CasoId,
    string Radicado,
    string Regla,
    string BaseLegal,
    Guid? ReferenciaId,
    DateTimeOffset IniciaEn,
    DateTimeOffset VenceEn,
    DateTimeOffset AlertaEn,
    string Estado,
    bool Extemporaneo,
    DateTimeOffset? CumplidoEn);

/// <summary>
/// Los plazos rastreados por el contexto de <c>halsa_app</c>, que los abre y los cumple. La RLS los limita al tenant de
/// la sesión (el del token, o el de <c>ISystemTenantScope</c> en un handler).
/// </summary>
public interface IPlazosRepository
{
    Task<List<Plazo>> DelCasoAsync(CasoMedicoId casoId, CancellationToken ct);

    Task<Plazo?> GetAsync(CasoMedicoId casoId, PlazoId id, CancellationToken ct);

    void Agregar(Plazo plazo);
}

/// <summary>Las reglas y los festivos de <c>sst</c>, globales.</summary>
public interface ICatalogoTerminos
{
    /// <summary>La versión de <paramref name="codigo"/> vigente en <paramref name="iniciaEn"/>. Lanza si no hay.</summary>
    Task<ReglaTermino> ReglaVigenteAsync(string codigo, DateTimeOffset iniciaEn, CancellationToken ct);

    /// <summary>Una calculadora con los festivos de <c>sst.festivos</c> (calculados y manuales).</summary>
    Task<CalculadoraTerminos> CalculadoraAsync(CancellationToken ct);
}

public static class PlazosMapeo
{
    public static PlazoResumen Resumen(Plazo plazo, string? baseLegal)
    {
        ArgumentNullException.ThrowIfNull(plazo);

        return new PlazoResumen(plazo.ReglaCodigo, baseLegal, plazo.VenceEn);
    }

    public static PlazoVencidoIntegrationEvent Vencido(Plazo plazo, string? baseLegal)
    {
        ArgumentNullException.ThrowIfNull(plazo);

        return new PlazoVencidoIntegrationEvent(
            plazo.Id.Value, plazo.TenantId.Value, plazo.CasoMedicoId.Value, plazo.Radicado, plazo.ReferenciaId,
            Resumen(plazo, baseLegal));
    }
}
