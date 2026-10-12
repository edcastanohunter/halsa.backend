using Halsa.Backend.Application.Integraciones.Canonico;
using Halsa.Backend.Application.Integraciones.Registros;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Primitives;
using Microsoft.EntityFrameworkCore.Storage;

namespace Halsa.Backend.Application.Abstractions.Integraciones;

/// <summary>
/// La unidad de trabajo de la ingesta (SPEC 05), conectada como <c>halsa_ingesta</c>: escribe el caso, el
/// reporte, el mensaje crudo, el <c>REPORTE_INICIAL</c> clínico, la solicitud, el outbox y la auditoría en
/// una sola transacción. Sobre la historia clínica solo puede insertar: no hay forma de leerla desde aquí.
///
/// Solo la usa <c>Halsa.Backend.Application.Integraciones</c> (test de arquitectura).
/// </summary>
public interface IIngestaDbContext
{
    void Add(CasoMedico caso);

    void Add(ReporteEvento reporte);

    void Add(SolicitudIntegracion solicitud);

    void Add(MensajeEntrante mensaje);

    void Add(HistoriaClinicaDetalle registro);

    /// <summary>Encola el evento en el outbox, dentro de la misma transacción.</summary>
    void Encolar(IIntegrationEvent evento);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct);

    /// <summary>
    /// Guarda lo agregado. Un choque con la clave de negocio <c>(tenant, tipo, numeroReporteOrigen)</c> devuelve
    /// <see cref="ResultadoGuardado.ReporteYaRadicado"/> en vez de lanzar.
    /// </summary>
    Task<ResultadoGuardado> GuardarAsync(CancellationToken ct);

    /// <summary>El radicado del caso que ya radicó ese reporte en el tenant actual, o <c>null</c>.</summary>
    Task<string?> RadicadoDelReporteAsync(TipoReporte tipo, string numeroReporteOrigen, CancellationToken ct);
}

public enum ResultadoGuardado
{
    Guardado,
    ReporteYaRadicado
}
