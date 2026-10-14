using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Infrastructure.Database.Auditoria;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Infrastructure.Auditoria;

/// <summary>
/// Arma la fila completa (actor, hora del servidor, códigos y hash). Lo comparten los dos
/// <c>AuditTrail</c> y <see cref="AuditWriter"/>, así que todas las filas salen con la misma forma.
/// </summary>
internal sealed class AccesoAuditadoFactory(IAuditActorProvider actorProvider, IClock clock)
{
    private const long TicksPorMicrosegundo = TimeSpan.TicksPerMillisecond / 1000;

    /// <param name="motivo">Por qué se denegó o se permitió excepcionalmente (<c>step-up</c>, SPEC 07). Casi siempre <c>null</c>.</param>
    public AccesoAuditado Crear(
        AccionAuditoria accion,
        ObjetivoAuditoria objetivo,
        TenantId? tenant,
        ResultadoAuditoria resultado,
        string? motivo = null)
    {
        ArgumentNullException.ThrowIfNull(objetivo);

        ActorAuditoria actor = actorProvider.Actual();

        var acceso = new AccesoAuditado
        {
            Id = Guid.CreateVersion7(),
            OcurridoEn = AMicrosegundos(clock.UtcNow),
            TenantId = tenant?.Value,
            ActorId = actor.ActorId,
            ActorTipo = actor.ActorTipo,
            ActorTenantId = actor.ActorTenantId,
            ActorIpsId = actor.ActorIpsId,
            ActorRoles = actor.Roles,
            ClientId = actor.ClientId,
            Ip = actor.Ip,
            UserAgent = actor.UserAgent,
            CorrelationId = actor.CorrelationId,
            Accion = AuditoriaCodigos.Codigo(accion),
            Resultado = AuditoriaCodigos.Codigo(resultado),
            RecursoTipo = AuditoriaCodigos.Codigo(objetivo.Tipo),
            RecursoId = objetivo.RecursoId,
            CasoMedicoId = objetivo.CasoMedicoId,
            Motivo = motivo,
            Acr = actor.Acr
        };

        acceso.CalcularHash();
        return acceso;
    }

    /// <summary><c>timestamptz</c> guarda microsegundos: sin truncar, el hash no se podría recalcular al leer.</summary>
    private static DateTimeOffset AMicrosegundos(DateTime utc) =>
        new(utc.Ticks - (utc.Ticks % TicksPorMicrosegundo), TimeSpan.Zero);
}
