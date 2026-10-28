using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Application.Abstractions.Integraciones;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Casos;
using Halsa.Backend.Application.Integraciones.Eventos;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Primitives;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Halsa.Backend.Application.Integraciones.Validacion;

/// <summary>
/// La validación asíncrona de una radicación (SPEC 05), con <c>halsa_app</c> y el actor de sistema del tenant:
/// <list type="number">
///   <item>Deduplicación: otro caso del mismo tenant, tipo y trabajador, no rechazado, radicado antes y con el
///   evento dentro de <see cref="IntegracionesOptions.VentanaDuplicado"/>, lo rechaza como duplicado.</item>
///   <item>Afiliación: si el core de la ARL dice que no está afiliado, se rechaza. Si el core falla, este handler
///   lanza y el outbox reintenta. Si no hay core integrado (<see cref="ResultadoAfiliacion.SinVerificar"/>), el caso
///   queda <c>Radicado</c> y el mensaje se da por procesado.</item>
///   <item>Si no aplica ninguna, el caso queda validado.</item>
/// </list>
/// El cambio de estado, sus eventos en el outbox y la auditoría se confirman juntos. Un evento repetido encuentra
/// el caso ya fuera de <c>Radicado</c> y no hace nada.
/// </summary>
internal sealed partial class ValidarCasoRadicadoHandler(
    ISystemTenantScope systemTenantScope,
    ICasoMedicoRepository repository,
    IReadDbContext readDb,
    IAfiliacionGateway afiliacion,
    IIntegrationEventPublisher publisher,
    IAuditTrail auditTrail,
    IUnitOfWork unitOfWork,
    IntegracionesOptions options,
    TimeProvider time,
    ILogger<ValidarCasoRadicadoHandler> logger)
    : IIntegrationEventHandler<CasoRadicadoIntegrationEvent>
{
    public async Task HandleAsync(CasoRadicadoIntegrationEvent evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evento);

        var tenant = new TenantId(evento.TenantId);
        using IDisposable _ = systemTenantScope.Entrar(tenant);

        // Committed in the same transaction as the event: if it is not visible, something is wrong. Retry.
        CasoMedico caso = await repository.GetByIdAsync(new CasoMedicoId(evento.CasoId), cancellationToken)
            ?? throw new InvalidOperationException($"El caso radicado {evento.CasoId} no existe en {evento.TenantId}.");

        if (caso.Estado != EstadoCaso.Radicado)
        {
            LogYaValidado(logger, caso.Id.Value, caso.Estado);
            return;
        }

        DateTimeOffset ahora = time.GetUtcNow();
        Result transicion;

        CasoMedicoId? duplicadoDe = await DuplicadoDeAsync(caso, tenant, cancellationToken);
        if (duplicadoDe is not null)
        {
            transicion = caso.Rechazar(MotivoRechazo.Duplicado, duplicadoDe, ahora);
        }
        else
        {
            ResultadoAfiliacion resultado = await afiliacion.VerificarAsync(
                tenant, caso.Trabajador, caso.Empleador, caso.FechaEvento, cancellationToken);

            // No ARL core to ask yet: the message is done and the case waits in Radicado, with nothing to save.
            if (resultado == ResultadoAfiliacion.SinVerificar)
            {
                LogAfiliacionPendiente(logger, caso.Id.Value);
                return;
            }

            transicion = resultado == ResultadoAfiliacion.NoAfiliado
                ? caso.Rechazar(MotivoRechazo.NoAfiliado, duplicadoDe: null, ahora)
                : caso.Validar(ahora);
        }

        if (transicion.IsFailure)
        {
            throw new InvalidOperationException($"No se pudo cerrar la validación del caso {caso.Id}: {transicion.Error}");
        }

        foreach (IDomainEvent domainEvent in caso.DomainEvents)
        {
            await publisher.EnqueueAsync(AIntegracion(domainEvent, evento.NumeroReporteOrigen), cancellationToken);
        }

        caso.ClearDomainEvents();

        auditTrail.Registrar(
            AccionAuditoria.Actualizar,
            new ObjetivoAuditoria(RecursoAuditoria.Caso, caso.Id.Value.ToString(), caso.Id.Value),
            tenant);

        // A concurrent delivery of the same event fails here on xmin and is retried: it then finds the case closed.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        LogValidado(logger, caso.Id.Value, caso.Estado, caso.MotivoRechazo);
    }

    private async Task<CasoMedicoId?> DuplicadoDeAsync(CasoMedico caso, TenantId tenant, CancellationToken ct)
    {
        DateTimeOffset desde = caso.FechaEvento - options.VentanaDuplicado;
        DateTimeOffset hasta = caso.FechaEvento + options.VentanaDuplicado;

        var anterior = await readDb.CasosMedicos
            .AsNoTracking()
            .Where(c => c.TenantId == tenant
                        && c.Id != caso.Id
                        && c.TipoEvento == caso.TipoEvento
                        && c.Trabajador.Tipo == caso.Trabajador.Tipo
                        && c.Trabajador.Numero == caso.Trabajador.Numero
                        && c.Estado != EstadoCaso.Rechazado
                        && c.RadicadoEn < caso.RadicadoEn
                        && c.FechaEvento >= desde
                        && c.FechaEvento <= hasta)
            .OrderBy(c => c.RadicadoEn)
            .Select(c => new { c.Id })
            .FirstOrDefaultAsync(ct);

        return anterior?.Id;
    }

    private static IIntegrationEvent AIntegracion(IDomainEvent domainEvent, string numeroReporteOrigen) => domainEvent switch
    {
        CasoValidadoDomainEvent validado => new CasoValidadoIntegrationEvent(
            validado.CasoId.Value, validado.TenantId, validado.Radicado, numeroReporteOrigen),

        CasoRechazadoDomainEvent rechazado => new CasoRechazadoIntegrationEvent(
            rechazado.CasoId.Value, rechazado.TenantId, rechazado.Radicado, numeroReporteOrigen,
            AuditoriaCodigos.Normalizar<MotivoRechazo>(rechazado.Motivo.ToString())!,
            rechazado.DuplicadoDe?.Value),

        _ => throw new InvalidOperationException($"La validación no publica {domainEvent.GetType().Name}.")
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Caso {CasoId} ya validado ({Estado}); el evento repetido se ignora")]
    private static partial void LogYaValidado(ILogger logger, Guid casoId, EstadoCaso estado);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Caso {CasoId}: sin core de afiliación integrado, queda Radicado hasta que se pueda verificar")]
    private static partial void LogAfiliacionPendiente(ILogger logger, Guid casoId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Caso {CasoId} validado: {Estado} {MotivoRechazo}")]
    private static partial void LogValidado(ILogger logger, Guid casoId, EstadoCaso estado, MotivoRechazo? motivoRechazo);
}
