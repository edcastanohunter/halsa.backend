using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Dictamenes;
using Halsa.Backend.Application.Incapacidades;
using Halsa.Backend.Application.Integraciones.Eventos;
using Halsa.Backend.Application.Pacientes;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Incapacidades;
using Halsa.Backend.Domain.Pacientes;
using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Halsa.Backend.Application.Plazos;

// La apertura de los plazos (SPEC 09): asíncrona, desde el outbox, con halsa_app y el actor de sistema del tenant del
// evento. Idempotente: el índice único (caso, regla, referencia) impide duplicar, y cada handler mira primero lo que ya
// existe. Si dos entregas del mismo evento corren a la vez, la segunda falla en el índice y el outbox la reintenta.

/// <summary>
/// La radicación de un FURAT o FUREP abre sus plazos, que inician en la fecha del evento. Los de reporte se evalúan
/// enseguida contra <c>radicado_en</c>: a tiempo quedan <c>CUMPLIDO</c>; tarde, <c>VENCIDO</c> y extemporáneos, con su
/// <see cref="PlazoVencidoIntegrationEvent"/>. Un AT abre además <c>INVESTIGACION_AT</c> y, si es grave o mortal,
/// <c>REMISION_INVESTIGACION_GRAVE</c>. Si el caso ya está rechazado, los plazos que quedarían abiertos nacen suspendidos.
/// </summary>
internal sealed partial class AbrirPlazosRadicacionHandler(
    ISystemTenantScope systemTenantScope,
    ITenantContext tenantContext,
    IReadDbContext readDb,
    IPlazosRepository plazos,
    ICatalogoTerminos catalogo,
    IIntegrationEventPublisher publisher,
    IUnitOfWork unitOfWork,
    ILogger<AbrirPlazosRadicacionHandler> logger)
    : IIntegrationEventHandler<CasoRadicadoIntegrationEvent>
{
    public async Task HandleAsync(CasoRadicadoIntegrationEvent evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evento);

        using IDisposable _ = systemTenantScope.Entrar(new TenantId(evento.TenantId));

        var casoId = new CasoMedicoId(evento.CasoId);
        var caso = await readDb.CasosMedicos
            .AsNoTracking()
            .Where(c => c.Id == casoId)
            .Select(c => new { c.TipoEvento, c.FechaEvento, c.RadicadoEn, c.EsGraveOMortal, c.Radicado, c.Estado, c.TenantId })
            .FirstOrDefaultAsync(cancellationToken)
            // Committed in the same transaction as the event: if it is not visible, something is wrong. Retry.
            ?? throw new InvalidOperationException($"El caso radicado {evento.CasoId} no existe en {evento.TenantId}.");

        string[] codigos = caso.TipoEvento == TipoEvento.AccidenteTrabajo
            ? caso.EsGraveOMortal
                ? [CodigosRegla.ReporteFurat, CodigosRegla.InvestigacionAt, CodigosRegla.RemisionInvestigacionGrave]
                : [CodigosRegla.ReporteFurat, CodigosRegla.InvestigacionAt]
            : [CodigosRegla.ReporteFurep];

        HashSet<string> existentes = [.. (await plazos.DelCasoAsync(casoId, cancellationToken))
            .Where(p => p.ReferenciaId is null)
            .Select(p => p.ReglaCodigo)];

        if (codigos.All(existentes.Contains))
        {
            LogYaAbiertos(logger, evento.CasoId);
            return;
        }

        CalculadoraTerminos calculadora = await catalogo.CalculadoraAsync(cancellationToken);

        foreach (string codigo in codigos.Where(c => !existentes.Contains(c)))
        {
            ReglaTermino regla = await catalogo.ReglaVigenteAsync(codigo, caso.FechaEvento, cancellationToken);
            var plazo = Plazo.Abrir(caso.TenantId, casoId, caso.Radicado, regla, null, caso.FechaEvento, calculadora);

            if (codigo is CodigosRegla.ReporteFurat or CodigosRegla.ReporteFurep
                && plazo.EvaluarRadicacion(caso.RadicadoEn, tenantContext.Sub))
            {
                await publisher.EnqueueAsync(PlazosMapeo.Vencido(plazo, regla.BaseLegal), cancellationToken);
            }

            if (caso.Estado == EstadoCaso.Rechazado)
            {
                plazo.Suspender();
            }

            plazos.Agregar(plazo);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        LogAbiertos(logger, evento.CasoId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Plazos de la radicación del caso {CasoId} abiertos")]
    private static partial void LogAbiertos(ILogger logger, Guid casoId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Los plazos del caso {CasoId} ya estaban abiertos; el evento repetido se ignora")]
    private static partial void LogYaAbiertos(ILogger logger, Guid casoId);
}

/// <summary>
/// Cada incapacidad de una cadena abre o recalcula su <c>TOPE_IT_180</c> (<c>referencia_id</c> = la cadena):
/// <c>vence_en = fecha_fin + (180 − días acumulados)</c> y la alerta 30 días antes. Una entrega vieja (con un
/// <c>fecha_fin</c> anterior al último visto) no lo retrocede, y uno que ya no está abierto no cambia.
/// </summary>
internal sealed class AbrirTopeIncapacidadHandler(
    ISystemTenantScope systemTenantScope,
    IReadDbContext readDb,
    IPlazosRepository plazos,
    ICatalogoTerminos catalogo,
    IUnitOfWork unitOfWork)
    : IIntegrationEventHandler<IncapacidadRegistradaIntegrationEvent>
{
    public async Task HandleAsync(IncapacidadRegistradaIntegrationEvent evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evento);

        using IDisposable _ = systemTenantScope.Entrar(new TenantId(evento.TenantId));

        var casoId = new CasoMedicoId(evento.CasoId);
        var incapacidadId = new IncapacidadId(evento.IncapacidadId);

        var incapacidad = await readDb.Incapacidades
            .Where(i => i.Id == incapacidadId && i.CasoMedicoId == casoId)
            .Select(i => new { i.CadenaId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"La incapacidad {evento.IncapacidadId} no existe en {evento.TenantId}.");
        IncapacidadId cadenaId = incapacidad.CadenaId;

        Plazo? tope = (await plazos.DelCasoAsync(casoId, cancellationToken))
            .FirstOrDefault(p => p.ReglaCodigo == CodigosRegla.TopeIt180 && p.ReferenciaId == cadenaId.Value);

        if (tope is not null)
        {
            if (tope.Estado != EstadoPlazo.Abierto || tope.UltimaFechaFin >= evento.FechaFin)
            {
                return;
            }

            ReglaTermino vigente = await catalogo.ReglaVigenteAsync(CodigosRegla.TopeIt180, tope.IniciaEn, cancellationToken);
            tope.RecalcularTope(vigente, evento.FechaFin, evento.DiasAcumulados);
        }
        else
        {
            DateOnly inicioCadena = await readDb.Incapacidades
                .Where(i => i.Id == cadenaId)
                .Select(i => i.FechaInicio)
                .FirstAsync(cancellationToken);

            string radicado = await readDb.CasosMedicos
                .AsNoTracking()
                .Where(c => c.Id == casoId)
                .Select(c => c.Radicado)
                .FirstAsync(cancellationToken);

            // The chain starts at the beginning of its first day, in Colombia.
            var iniciaEn = new DateTimeOffset(inicioCadena.ToDateTime(TimeOnly.MinValue), CalculadoraTerminos.ZonaColombia);
            ReglaTermino regla = await catalogo.ReglaVigenteAsync(CodigosRegla.TopeIt180, iniciaEn, cancellationToken);

            plazos.Agregar(Plazo.AbrirTope(
                new TenantId(evento.TenantId), casoId, radicado, regla, cadenaId.Value, iniciaEn,
                evento.FechaFin, evento.DiasAcumulados));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>La notificación del dictamen PCL abre su <c>CONTROVERSIA_DICTAMEN</c> (<c>referencia_id</c> = el dictamen).</summary>
internal sealed class AbrirControversiaDictamenHandler(
    ISystemTenantScope systemTenantScope,
    IReadDbContext readDb,
    IPlazosRepository plazos,
    ICatalogoTerminos catalogo,
    IUnitOfWork unitOfWork)
    : IIntegrationEventHandler<DictamenPclNotificadoIntegrationEvent>
{
    public async Task HandleAsync(DictamenPclNotificadoIntegrationEvent evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evento);

        using IDisposable _ = systemTenantScope.Entrar(new TenantId(evento.TenantId));

        var casoId = new CasoMedicoId(evento.CasoId);

        bool existe = (await plazos.DelCasoAsync(casoId, cancellationToken))
            .Any(p => p.ReglaCodigo == CodigosRegla.ControversiaDictamen && p.ReferenciaId == evento.DictamenId);
        if (existe)
        {
            return;
        }

        string radicado = await readDb.CasosMedicos
            .AsNoTracking()
            .Where(c => c.Id == casoId)
            .Select(c => c.Radicado)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"El caso {evento.CasoId} del dictamen no existe en {evento.TenantId}.");

        ReglaTermino regla = await catalogo.ReglaVigenteAsync(CodigosRegla.ControversiaDictamen, evento.NotificadoEn, cancellationToken);
        CalculadoraTerminos calculadora = await catalogo.CalculadoraAsync(cancellationToken);

        plazos.Agregar(Plazo.Abrir(
            new TenantId(evento.TenantId), casoId, radicado, regla, evento.DictamenId, evento.NotificadoEn, calculadora));

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Un caso rechazado (duplicado o sin afiliación) deja de correr plazos: los abiertos pasan a <c>SUSPENDIDO</c>.</summary>
internal sealed class SuspenderPlazosCasoRechazadoHandler(
    ISystemTenantScope systemTenantScope,
    IPlazosRepository plazos,
    IUnitOfWork unitOfWork)
    : IIntegrationEventHandler<CasoRechazadoIntegrationEvent>
{
    public async Task HandleAsync(CasoRechazadoIntegrationEvent evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evento);

        using IDisposable _ = systemTenantScope.Entrar(new TenantId(evento.TenantId));

        int suspendidos = (await plazos.DelCasoAsync(new CasoMedicoId(evento.CasoId), cancellationToken))
            .Count(p => p.Suspender());

        if (suspendidos > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>
/// Una solicitud de copia de la HC (SPEC 12) abre su <c>SOLICITUD_COPIA_HC</c> (<c>referencia_id</c> = la solicitud), que
/// inicia al solicitarla. El cierre de la solicitud lo cumple; si la ARL la cerró antes de que este evento llegara, el
/// plazo nace cumplido, con el instante y el actor del cierre.
/// </summary>
internal sealed class AbrirPlazoSolicitudCopiaHcHandler(
    ISystemTenantScope systemTenantScope,
    ITenantContext tenantContext,
    IReadDbContext readDb,
    IPlazosRepository plazos,
    ICatalogoTerminos catalogo,
    IUnitOfWork unitOfWork,
    TimeProvider time)
    : IIntegrationEventHandler<SolicitudCopiaHcCreadaIntegrationEvent>
{
    public async Task HandleAsync(SolicitudCopiaHcCreadaIntegrationEvent evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evento);

        var tenant = new TenantId(evento.TenantId);
        using IDisposable _ = systemTenantScope.Entrar(tenant);

        var casoId = new CasoMedicoId(evento.CasoMedicoId);

        bool existe = (await plazos.DelCasoAsync(casoId, cancellationToken))
            .Any(p => p.ReglaCodigo == CodigosRegla.SolicitudCopiaHc && p.ReferenciaId == evento.SolicitudId);
        if (existe)
        {
            return;
        }

        var solicitudId = new SolicitudCopiaHcId(evento.SolicitudId);
        var solicitud = await readDb.SolicitudesCopiaHc
            .Where(s => s.Id == solicitudId)
            .Select(s => new { s.Estado, s.AtendidaEn, s.AtendidaPor })
            .FirstOrDefaultAsync(cancellationToken)
            // Committed in the same transaction as the event: if it is not visible, something is wrong. Retry.
            ?? throw new InvalidOperationException($"La solicitud {evento.SolicitudId} no existe en {evento.TenantId}.");

        string radicado = await readDb.CasosMedicos
            .AsNoTracking()
            .Where(c => c.Id == casoId)
            .Select(c => c.Radicado)
            .FirstAsync(cancellationToken);

        ReglaTermino regla = await catalogo.ReglaVigenteAsync(CodigosRegla.SolicitudCopiaHc, evento.SolicitadaEn, cancellationToken);
        CalculadoraTerminos calculadora = await catalogo.CalculadoraAsync(cancellationToken);

        var plazo = Plazo.Abrir(tenant, casoId, radicado, regla, evento.SolicitudId, evento.SolicitadaEn, calculadora);

        if (solicitud.Estado is EstadoSolicitudCopiaHc.Entregada or EstadoSolicitudCopiaHc.Rechazada)
        {
            DateTimeOffset ahora = time.GetUtcNow();
            plazo.Cumplir(
                CodigosSolicitudCopiaHc.NotaPlazo(solicitud.Estado),
                solicitud.AtendidaEn ?? ahora,
                solicitud.AtendidaPor ?? tenantContext.Sub,
                ahora);
        }

        plazos.Agregar(plazo);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
