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

namespace Halsa.Backend.FunctionalTests.OpenApi;

/// <summary>
/// SPEC 10, paso 8: cada webhook de <c>docs/api/atel-api.openapi.yaml</c> describe exactamente el payload que envía el
/// Worker. El contrato y <c>PayloadsWebhook</c> no pueden separarse sin que esto falle.
/// </summary>
public sealed class ContratoWebhooksTests
{
    private static readonly Guid Caso = Guid.CreateVersion7();

    private const string Radicado = "ATEL-ARL_A-2026-000001";

    private static readonly PlazoResumen Plazo = new("INVESTIGACION_AT", "Res. 1401/2007", DateTimeOffset.UtcNow);

    public static TheoryData<string, string, string> Webhooks() => new()
    {
        { "plazoProximoAVencer", "EventoPlazo",
            PayloadsWebhook.Desde(new PlazoProximoAVencerIntegrationEvent(Guid.NewGuid(), "ARL_A", Caso, Radicado, null, Plazo)).Payload },
        { "plazoVencido", "EventoPlazo",
            PayloadsWebhook.Desde(new PlazoVencidoIntegrationEvent(Guid.NewGuid(), "ARL_A", Caso, Radicado, null, Plazo)).Payload },
        { "casoValidado", "EventoCasoValidado",
            PayloadsWebhook.Desde(new CasoValidadoIntegrationEvent(Caso, "ARL_A", Radicado, "FURAT-1")).Payload },
        { "casoRechazado", "EventoCasoRechazado",
            PayloadsWebhook.Desde(new CasoRechazadoIntegrationEvent(Caso, "ARL_A", Radicado, "FURAT-1", "NO_AFILIADO", null)).Payload },
        { "incapacidadRegistrada", "EventoIncapacidadRegistrada",
            PayloadsWebhook.Desde(new IncapacidadRegistradaIntegrationEvent(
                Guid.NewGuid(), Caso, "ARL_A", "PRORROGA", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10), 10, 40, []), Radicado).Payload },
        { "conceptoRegistrado", "EventoConceptoRegistrado",
            PayloadsWebhook.Desde(new RegistroClinicoCreadoIntegrationEvent(Guid.NewGuid(), Caso, "ARL_A", "EVOLUCION", null), Radicado).Payload },
        { "dictamenNotificado", "EventoDictamenNotificado",
            PayloadsWebhook.Desde(new DictamenPclNotificadoIntegrationEvent(
                Guid.NewGuid(), Caso, "ARL_A", DateTimeOffset.UtcNow, "PERSONAL", new DateOnly(2026, 10, 1), 12m, "COMUN"), Radicado).Payload },
        { "auditoriaAccesoEmergencia", "EventoAccesoEmergencia",
            PayloadsWebhook.Desde(new AccesoEmergenciaRegistradoIntegrationEvent(
                Guid.NewGuid(), Caso, "ARL_A", "sub", "Urgencia vital sin asignación previa.", DateTimeOffset.UtcNow), Radicado).Payload },
        { "casoRemitido", "EventoCasoRemitido",
            PayloadsWebhook.Desde(new CasoRemitidoIntegrationEvent(
                Caso, "ARL_A", Radicado, Guid.NewGuid(), "110011234501", "EnfermedadLaboral", DateTimeOffset.UtcNow, "CC", "1")).Payload },
        { "remisionRevocada", "EventoRemisionRevocada",
            PayloadsWebhook.Desde(new RemisionRevocadaIntegrationEvent(Caso, "ARL_A", Radicado, Guid.NewGuid(), "110011234501")).Payload }
    };

    [Theory]
    [MemberData(nameof(Webhooks))]
    public void EveryWebhook_ShouldBeInTheContract_WithExactlyThePayloadsFields(string webhook, string esquema, string payload)
    {
        string[] lineas = File.ReadAllLines(Contrato.Path);
        int seccion = Array.IndexOf(lineas, "webhooks:");
        seccion.ShouldBeGreaterThan(0);
        int fin = Array.IndexOf(lineas, "components:", seccion);

        string[] webhooks = lineas[seccion..fin];
        int entrada = Array.IndexOf(webhooks, $"  {webhook}:");
        entrada.ShouldBeGreaterThan(0, $"webhooks.{webhook} is not in the contract");
        webhooks.Skip(entrada + 1).TakeWhile(l => !(l.StartsWith("  ", StringComparison.Ordinal) && l[2] != ' '))
            .ShouldContain(l => l.Contains($"#/components/schemas/{esquema}'", StringComparison.Ordinal));

        Contrato.PropiedadesDelContrato(esquema)
            .ShouldBe(JsonNode.Parse(payload)!.AsObject().Select(p => p.Key), ignoreOrder: true);
    }

    [Fact]
    public void TheContract_ShouldHaveExactlyTheTenWebhooks()
    {
        string[] lineas = File.ReadAllLines(Contrato.Path);
        int seccion = Array.IndexOf(lineas, "webhooks:");
        int fin = Array.IndexOf(lineas, "components:", seccion);

        lineas[(seccion + 1)..fin]
            .Where(l => l.Length > 3 && l.StartsWith("  ", StringComparison.Ordinal) && l[2] != ' ')
            .Select(l => l.Trim().TrimEnd(':'))
            .ShouldBe(Webhooks().Select(f => (string)f[0]), ignoreOrder: true);
    }
}
