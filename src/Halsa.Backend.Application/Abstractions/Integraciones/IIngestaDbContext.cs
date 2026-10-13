using Halsa.Backend.Application.Abstractions.Clinico;
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
/// Desde la SPEC 06 también recibe los conceptos médicos de las IPS: lee el caso remitido y pregunta si un
/// registro existe, sin leer su contenido.
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

    /// <summary>
    /// El caso, si el actor lo ve (SPEC 06). Pasa por la RLS de <c>casos_medicos</c>: a una IPS solo le devuelve los
    /// casos que tiene remitidos y vigentes. Dentro de la transacción, si hay una abierta.
    /// </summary>
    Task<CasoVisible?> CasoVisibleAsync(CasoMedicoId casoId, CancellationToken ct);

    /// <summary>
    /// Si el registro existe en ese caso y el actor lo ve (SPEC 06, <c>corrigeA</c>). Solo responde sí o no: la
    /// ingesta sigue sin poder leer la historia clínica.
    /// </summary>
    Task<bool> RegistroVisibleAsync(CasoMedicoId casoId, HistoriaClinicaDetalleId registroId, CancellationToken ct);
}

public enum ResultadoGuardado
{
    Guardado,
    ReporteYaRadicado,

    /// <summary>
    /// La RLS rechazó una fila (SPEC 06): la remisión de la IPS se revocó entre la lectura del caso y el insert.
    /// No se guardó nada.
    /// </summary>
    FueraDeAlcance
}
