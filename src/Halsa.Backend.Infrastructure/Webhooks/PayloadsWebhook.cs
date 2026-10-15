using System.Text.Json;
using Halsa.Backend.Application.Auditoria.RevisionesEmergencia;
using Halsa.Backend.Application.Casos;
using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Application.Dictamenes;
using Halsa.Backend.Application.Incapacidades;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Application.Integraciones.Eventos;
using Halsa.Backend.Application.Plazos;
using Halsa.Backend.Domain.Webhooks;
using Halsa.Backend.SharedKernel.Primitives;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Infrastructure.Webhooks;

/// <summary>
/// Un evento listo para el fan-out: su tipo público, el emisor de las suscripciones que lo reciben, el id estable con que
/// el receptor deduplica (el del mensaje del outbox) y el cuerpo exacto que se firma.
/// </summary>
internal sealed record EventoPublico(string Tipo, string Emisor, Guid EventoId, string Payload);

/// <summary>
/// La conversión de cada evento de integración a su payload público (SPEC 10). Cada payload es un tipo explícito con solo
/// los campos de la tabla de la spec: ningún diagnóstico, contenido clínico ni fundamento puede colarse por accidente.
/// </summary>
internal static class PayloadsWebhook
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── ARL ───────────────────────────────────────────────────────────────────

    public static EventoPublico Desde(PlazoProximoAVencerIntegrationEvent e) => Arl(e, e.TenantId,
        new PayloadPlazo(e.EventId, EventosWebhook.PlazoProximoAVencer, Ocurrido(e), e.CasoMedicoId, e.Radicado, e.Plazo));

    public static EventoPublico Desde(PlazoVencidoIntegrationEvent e) => Arl(e, e.TenantId,
        new PayloadPlazo(e.EventId, EventosWebhook.PlazoVencido, Ocurrido(e), e.CasoMedicoId, e.Radicado, e.Plazo));

    public static EventoPublico Desde(CasoValidadoIntegrationEvent e) => Arl(e, e.TenantId,
        new PayloadCasoValidado(e.EventId, EventosWebhook.CasoValidado, Ocurrido(e), e.CasoId, e.Radicado, e.NumeroReporteOrigen));

    public static EventoPublico Desde(CasoRechazadoIntegrationEvent e) => Arl(e, e.TenantId,
        new PayloadCasoRechazado(
            e.EventId, EventosWebhook.CasoRechazado, Ocurrido(e), e.CasoId, e.Radicado, e.NumeroReporteOrigen, e.Motivo,
            e.DuplicadoDe));

    public static EventoPublico Desde(IncapacidadRegistradaIntegrationEvent e, string radicado) => Arl(e, e.TenantId,
        new PayloadIncapacidad(
            e.EventId, EventosWebhook.IncapacidadRegistrada, Ocurrido(e), e.CasoId, radicado, e.IncapacidadId, e.Tipo,
            e.FechaInicio, e.FechaFin, e.DiasAcumulados, e.Alertas));

    public static EventoPublico Desde(RegistroClinicoCreadoIntegrationEvent e, string radicado) => Arl(e, e.TenantId,
        new PayloadConcepto(
            e.EventId, EventosWebhook.ConceptoRegistrado, Ocurrido(e), e.CasoId, radicado, e.RegistroId, e.Tipo, e.IpsEmisora));

    public static EventoPublico Desde(DictamenPclNotificadoIntegrationEvent e, string radicado) => Arl(e, e.TenantId,
        new PayloadDictamen(
            e.EventId, EventosWebhook.DictamenNotificado, Ocurrido(e), e.CasoId, radicado, e.DictamenId, e.PorcentajePcl,
            e.Origen, e.NotificadoEn));

    public static EventoPublico Desde(AccesoEmergenciaRegistradoIntegrationEvent e, string radicado) => Arl(e, e.TenantId,
        new PayloadAccesoEmergencia(
            e.EventId, EventosWebhook.AccesoEmergencia, Ocurrido(e), e.CasoId, radicado, e.RevisionId, e.ActorId, e.Motivo,
            e.VenceRevisionEn));

    // ── IPS ───────────────────────────────────────────────────────────────────

    public static EventoPublico Desde(CasoRemitidoIntegrationEvent e) => Ips(e, e.IpsId,
        new PayloadCasoRemitido(
            e.EventId, EventosWebhook.CasoRemitido, Ocurrido(e), e.CasoId, e.Radicado, e.RemisionId, e.TipoEvento,
            e.FechaEvento, new IdentificacionPublica(e.TrabajadorTipo, e.TrabajadorNumero)));

    public static EventoPublico Desde(RemisionRevocadaIntegrationEvent e) => Ips(e, e.IpsId,
        new PayloadRemisionRevocada(e.EventId, EventosWebhook.RemisionRevocada, Ocurrido(e), e.CasoId, e.Radicado, e.RemisionId));

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EventoPublico Arl<TPayload>(IIntegrationEvent e, string tenantId, TPayload payload)
        where TPayload : IPayloadWebhook =>
        new(payload.Tipo, SuscripcionWebhook.EmisorDe(new TenantId(tenantId)), e.EventId, Serializar(payload));

    private static EventoPublico Ips<TPayload>(IIntegrationEvent e, string ipsId, TPayload payload)
        where TPayload : IPayloadWebhook =>
        new(payload.Tipo, SuscripcionWebhook.EmisorDe(new IpsId(ipsId)), e.EventId, Serializar(payload));

    private static string Serializar<TPayload>(TPayload payload) => JsonSerializer.Serialize(payload, Json);

    private static DateTimeOffset Ocurrido(IIntegrationEvent e) =>
        new(DateTime.SpecifyKind(e.OccurredOnUtc, DateTimeKind.Utc));
}

// The public contracts (docs/api/atel-api.openapi.yaml, section webhooks). Every one starts with eventoId, tipo and
// ocurridoEn; the rest is the spec's table, field by field.

internal interface IPayloadWebhook
{
    string Tipo { get; }
}

internal sealed record PayloadPlazo(
    Guid EventoId, string Tipo, DateTimeOffset OcurridoEn, Guid CasoId, string Radicado, PlazoResumen Plazo) : IPayloadWebhook;

internal sealed record PayloadCasoValidado(
    Guid EventoId, string Tipo, DateTimeOffset OcurridoEn, Guid CasoId, string Radicado, string NumeroReporteOrigen)
    : IPayloadWebhook;

internal sealed record PayloadCasoRechazado(
    Guid EventoId,
    string Tipo,
    DateTimeOffset OcurridoEn,
    Guid CasoId,
    string Radicado,
    string NumeroReporteOrigen,
    string Motivo,
    Guid? DuplicadoDe) : IPayloadWebhook;

internal sealed record PayloadIncapacidad(
    Guid EventoId,
    string Tipo,
    DateTimeOffset OcurridoEn,
    Guid CasoId,
    string Radicado,
    Guid IncapacidadId,
    // The spec's "tipo" of the incapacidad (INICIAL or PRORROGA): "tipo" is already the event's type.
    string TipoIncapacidad,
    DateOnly FechaInicio,
    DateOnly FechaFin,
    int DiasAcumulados,
    IReadOnlyList<string> Alertas) : IPayloadWebhook;

internal sealed record PayloadConcepto(
    Guid EventoId,
    string Tipo,
    DateTimeOffset OcurridoEn,
    Guid CasoId,
    string Radicado,
    Guid RegistroId,
    // The spec's "tipo" of the record (CONCEPTO_MEDICO, EVOLUCION or CONCEPTO_REHABILITACION), for the same reason.
    string TipoRegistro,
    string? IpsEmisora) : IPayloadWebhook;

internal sealed record PayloadDictamen(
    Guid EventoId,
    string Tipo,
    DateTimeOffset OcurridoEn,
    Guid CasoId,
    string Radicado,
    Guid DictamenId,
    decimal PorcentajePcl,
    string Origen,
    DateTimeOffset NotificadoEn) : IPayloadWebhook;

internal sealed record PayloadAccesoEmergencia(
    Guid EventoId,
    string Tipo,
    DateTimeOffset OcurridoEn,
    Guid CasoId,
    string Radicado,
    Guid RevisionId,
    string ActorId,
    string Motivo,
    DateTimeOffset VenceRevisionEn) : IPayloadWebhook;

/// <summary>El documento del trabajador: dato personal, no clínico.</summary>
internal sealed record IdentificacionPublica(string Tipo, string Numero);

internal sealed record PayloadCasoRemitido(
    Guid EventoId,
    string Tipo,
    DateTimeOffset OcurridoEn,
    Guid CasoId,
    string Radicado,
    Guid RemisionId,
    string TipoEvento,
    DateTimeOffset FechaEvento,
    IdentificacionPublica Trabajador) : IPayloadWebhook;

internal sealed record PayloadRemisionRevocada(
    Guid EventoId, string Tipo, DateTimeOffset OcurridoEn, Guid CasoId, string Radicado, Guid RemisionId)
    : IPayloadWebhook;
