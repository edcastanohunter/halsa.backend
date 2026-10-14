using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Dictamenes;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;

namespace Halsa.Backend.Application.Plazos;

/// <summary>
/// Al vencer <c>CONTROVERSIA_DICTAMEN</c> sin una controversia registrada (que ARQ 01 no tiene), el dictamen queda
/// <c>EN_FIRME</c> (SPEC 09): con <c>halsa_app</c> y el actor de sistema del tenant, auditado como
/// <c>ACTUALIZAR / CASO</c>. Los demás vencimientos no cambian nada aquí: su entrega como webhook es la SPEC 10.
/// Un evento repetido encuentra el dictamen ya en firme y no hace nada.
/// </summary>
internal sealed partial class PlazoVencidoHandler(
    ISystemTenantScope systemTenantScope,
    IDictamenPclRepository dictamenes,
    IAuditTrail auditTrail,
    IUnitOfWork unitOfWork,
    ILogger<PlazoVencidoHandler> logger)
    : IIntegrationEventHandler<PlazoVencidoIntegrationEvent>
{
    public async Task HandleAsync(PlazoVencidoIntegrationEvent evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evento);

        if (evento.Plazo.Regla != CodigosRegla.ControversiaDictamen || evento.ReferenciaId is not { } dictamenId)
        {
            return;
        }

        var tenant = new TenantId(evento.TenantId);
        using IDisposable _ = systemTenantScope.Entrar(tenant);

        var casoId = new CasoMedicoId(evento.CasoMedicoId);
        DictamenPcl dictamen = await dictamenes.GetAsync(casoId, new DictamenPclId(dictamenId), cancellationToken)
            // A notified dictamen cannot be corrected, so it is still there: if it is not visible, retry.
            ?? throw new InvalidOperationException($"El dictamen {dictamenId} del plazo {evento.PlazoId} no existe en {evento.TenantId}.");

        if (!dictamen.PonerEnFirme())
        {
            LogSinCambio(logger, dictamenId, dictamen.EstadoNotificacion);
            return;
        }

        auditTrail.Registrar(
            AccionAuditoria.Actualizar,
            new ObjetivoAuditoria(RecursoAuditoria.Caso, casoId.Value.ToString(), casoId.Value),
            tenant);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        LogEnFirme(logger, dictamenId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dictamen {DictamenId} en firme: venció su controversia")]
    private static partial void LogEnFirme(ILogger logger, Guid dictamenId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dictamen {DictamenId} en {Estado}: el vencimiento de la controversia no lo cambia")]
    private static partial void LogSinCambio(ILogger logger, Guid dictamenId, EstadoNotificacionDictamen estado);
}
