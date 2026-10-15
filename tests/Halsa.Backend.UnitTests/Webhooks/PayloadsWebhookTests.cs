using System.Text.Json.Nodes;
using Halsa.Backend.Application.Auditoria.RevisionesEmergencia;
using Halsa.Backend.Application.Casos;
using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Application.Dictamenes;
using Halsa.Backend.Application.Incapacidades;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Application.Integraciones.Eventos;
using Halsa.Backend.Application.Plazos;
using Halsa.Backend.Infrastructure.Webhooks;

namespace Halsa.Backend.UnitTests.Webhooks;

/// <summary>SPEC 10, paso 6: cada evento de integración y su payload público, campo por campo.</summary>
public sealed class PayloadsWebhookTests
{
    private static readonly Guid Caso = Guid.Parse("0199e3a0-0000-7000-8000-0000000000ca");

    private const string Radicado = "ATEL-ARL_A-2026-000001";

    private static readonly string[] Prohibidas = ["diagnosticos", "fundamentos", "contenido", "descripcion"];

    private static readonly PlazoResumen Plazo = new("INVESTIGACION_AT", "Res. 1401/2007", DateTimeOffset.UtcNow);

    /// <summary>The 10 public types, each with the exact keys of the spec's table.</summary>
    public static TheoryData<string, string, string, string[]> LosDiez() => new()
    {
        {
            "plazo.proximo-a-vencer", "tenant:ARL_A",
            PayloadsWebhook.Desde(new PlazoProximoAVencerIntegrationEvent(Guid.NewGuid(), "ARL_A", Caso, Radicado, null, Plazo)).Payload,
            ["casoId", "radicado", "plazo"]
        },
        {
            "plazo.vencido", "tenant:ARL_A",
            PayloadsWebhook.Desde(new PlazoVencidoIntegrationEvent(Guid.NewGuid(), "ARL_A", Caso, Radicado, null, Plazo)).Payload,
            ["casoId", "radicado", "plazo"]
        },
        {
            "caso.validado", "tenant:ARL_A",
            PayloadsWebhook.Desde(new CasoValidadoIntegrationEvent(Caso, "ARL_A", Radicado, "FURAT-1")).Payload,
            ["casoId", "radicado", "numeroReporteOrigen"]
        },
        {
            "caso.rechazado", "tenant:ARL_A",
            PayloadsWebhook.Desde(new CasoRechazadoIntegrationEvent(Caso, "ARL_A", Radicado, "FURAT-1", "DUPLICADO", Guid.NewGuid())).Payload,
            ["casoId", "radicado", "numeroReporteOrigen", "motivo", "duplicadoDe"]
        },
        {
            "incapacidad.registrada", "tenant:ARL_A",
            PayloadsWebhook.Desde(new IncapacidadRegistradaIntegrationEvent(
                Guid.NewGuid(), Caso, "ARL_A", "INICIAL", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10), 10, 10,
                ["CARGA_EXTEMPORANEA"]), Radicado).Payload,
            ["casoId", "radicado", "incapacidadId", "tipoIncapacidad", "fechaInicio", "fechaFin", "diasAcumulados", "alertas"]
        },
        {
            "concepto.registrado", "tenant:ARL_A",
            PayloadsWebhook.Desde(new RegistroClinicoCreadoIntegrationEvent(
                Guid.NewGuid(), Caso, "ARL_A", "CONCEPTO_MEDICO", "110011234501"), Radicado).Payload,
            ["casoId", "radicado", "registroId", "tipoRegistro", "ipsEmisora"]
        },
        {
            "dictamen.notificado", "tenant:ARL_A",
            PayloadsWebhook.Desde(new DictamenPclNotificadoIntegrationEvent(
                Guid.NewGuid(), Caso, "ARL_A", DateTimeOffset.UtcNow, "ELECTRONICO", new DateOnly(2026, 10, 1), 35.5m,
                "LABORAL"), Radicado).Payload,
            ["casoId", "radicado", "dictamenId", "porcentajePcl", "origen", "notificadoEn"]
        },
        {
            "auditoria.acceso-emergencia", "tenant:ARL_A",
            PayloadsWebhook.Desde(new AccesoEmergenciaRegistradoIntegrationEvent(
                Guid.NewGuid(), Caso, "ARL_A", "sub-medico", "Paciente inconsciente en urgencias, sin asignación.",
                DateTimeOffset.UtcNow.AddHours(72)), Radicado).Payload,
            ["casoId", "radicado", "revisionId", "actorId", "motivo", "venceRevisionEn"]
        },
        {
            "caso.remitido", "ips:110011234501",
            PayloadsWebhook.Desde(new CasoRemitidoIntegrationEvent(
                Caso, "ARL_A", Radicado, Guid.NewGuid(), "110011234501", "AccidenteTrabajo", DateTimeOffset.UtcNow, "CC",
                "1020304050")).Payload,
            ["casoId", "radicado", "remisionId", "tipoEvento", "fechaEvento", "trabajador"]
        },
        {
            "remision.revocada", "ips:110011234501",
            PayloadsWebhook.Desde(new RemisionRevocadaIntegrationEvent(Caso, "ARL_A", Radicado, Guid.NewGuid(), "110011234501")).Payload,
            ["casoId", "radicado", "remisionId"]
        }
    };

    [Theory]
    [MemberData(nameof(LosDiez))]
    public void EveryPayload_ShouldCarryExactlyTheFieldsOfTheSpec(string tipo, string emisor, string payload, string[] campos)
    {
        _ = emisor;
        JsonObject json = JsonNode.Parse(payload)!.AsObject();

        json.Select(p => p.Key).ShouldBe(["eventoId", "tipo", "ocurridoEn", .. campos], ignoreOrder: true);
        json["tipo"]!.GetValue<string>().ShouldBe(tipo);
        json["casoId"]!.GetValue<Guid>().ShouldBe(Caso);
        json["radicado"]!.GetValue<string>().ShouldBe(Radicado);
    }

    [Theory]
    [MemberData(nameof(LosDiez))]
    public void NoPayload_ShouldContainAClinicalKey_AtAnyDepth(string tipo, string emisor, string payload, string[] campos)
    {
        _ = (tipo, emisor, campos);

        Claves(JsonNode.Parse(payload)!).Where(c => Prohibidas.Contains(c, StringComparer.OrdinalIgnoreCase)).ShouldBeEmpty();
    }

    [Fact]
    public void TheCatalog_ShouldHaveTheTenTypesOfTheSpec()
    {
        LosDiez().Select(fila => (string)fila[0]).Order(StringComparer.Ordinal)
            .ShouldBe(Halsa.Backend.Domain.Webhooks.EventosWebhook.Catalogo.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EventoId_ShouldBeTheOutboxMessageId_AndTheEmisorTheRecipient()
    {
        var evento = new CasoRemitidoIntegrationEvent(
            Caso, "ARL_A", Radicado, Guid.NewGuid(), "110011234501", "AccidenteTrabajo", DateTimeOffset.UtcNow, "CC", "1");

        EventoPublico publico = PayloadsWebhook.Desde(evento);

        publico.EventoId.ShouldBe(evento.EventId);
        publico.Emisor.ShouldBe("ips:110011234501");
        JsonNode.Parse(publico.Payload)!["eventoId"]!.GetValue<Guid>().ShouldBe(evento.EventId);
        JsonNode.Parse(publico.Payload)!["trabajador"]!.ToJsonString().ShouldBe("""{"tipo":"CC","numero":"1"}""");
    }

    private static IEnumerable<string> Claves(JsonNode nodo) => nodo switch
    {
        JsonObject o => o.SelectMany(p => p.Value is null ? [p.Key] : Claves(p.Value).Prepend(p.Key)),
        JsonArray a => a.Where(n => n is not null).SelectMany(n => Claves(n!)),
        _ => []
    };
}
