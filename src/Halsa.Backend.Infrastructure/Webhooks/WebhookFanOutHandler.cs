using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Auditoria.RevisionesEmergencia;
using Halsa.Backend.Application.Casos;
using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Application.Dictamenes;
using Halsa.Backend.Application.Incapacidades;
using Halsa.Backend.Application.Integraciones.Eventos;
using Halsa.Backend.Application.Plazos;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Worker;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Halsa.Backend.Infrastructure.Webhooks;

/// <summary>
/// Del outbox a las entregas (SPEC 10): un <see cref="IIntegrationEventHandler{TEvent}"/> por cada tipo de origen, en el
/// Worker. Construye el payload público y, como <c>halsa_worker</c>, inserta una fila en
/// <c>integracion.entregas_webhook</c> por cada suscripción <c>ACTIVA</c> del destinatario que incluya el tipo. No envía
/// nada: un receptor lento no frena el drenado del outbox, de eso se ocupa <c>WebhookDispatcherJob</c>.
///
/// Idempotente por <c>(suscripcion_id, evento_id)</c>: un evento repetido no duplica entregas. El radicado que algunos
/// eventos no traen se lee como <c>halsa_app</c> en el tenant del evento, porque <c>halsa_worker</c> no ve <c>atel</c>.
/// </summary>
internal sealed class WebhookFanOutHandler(
    BaseDatosWorker baseDatos,
    ISystemTenantScope systemTenantScope,
    IReadDbContext readDb,
    TimeProvider time)
    : IIntegrationEventHandler<PlazoProximoAVencerIntegrationEvent>,
      IIntegrationEventHandler<PlazoVencidoIntegrationEvent>,
      IIntegrationEventHandler<CasoValidadoIntegrationEvent>,
      IIntegrationEventHandler<CasoRechazadoIntegrationEvent>,
      IIntegrationEventHandler<IncapacidadRegistradaIntegrationEvent>,
      IIntegrationEventHandler<RegistroClinicoCreadoIntegrationEvent>,
      IIntegrationEventHandler<DictamenPclNotificadoIntegrationEvent>,
      IIntegrationEventHandler<AccesoEmergenciaRegistradoIntegrationEvent>,
      IIntegrationEventHandler<CasoRemitidoIntegrationEvent>,
      IIntegrationEventHandler<RemisionRevocadaIntegrationEvent>
{
    /// <summary>The contracts this handler serves, for its registration.</summary>
    public static readonly Type[] Contratos = [.. typeof(WebhookFanOutHandler).GetInterfaces()
        .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>))];

    public Task HandleAsync(PlazoProximoAVencerIntegrationEvent evento, CancellationToken cancellationToken) =>
        InsertarAsync(PayloadsWebhook.Desde(evento), cancellationToken);

    public Task HandleAsync(PlazoVencidoIntegrationEvent evento, CancellationToken cancellationToken) =>
        InsertarAsync(PayloadsWebhook.Desde(evento), cancellationToken);

    public Task HandleAsync(CasoValidadoIntegrationEvent evento, CancellationToken cancellationToken) =>
        InsertarAsync(PayloadsWebhook.Desde(evento), cancellationToken);

    public Task HandleAsync(CasoRechazadoIntegrationEvent evento, CancellationToken cancellationToken) =>
        InsertarAsync(PayloadsWebhook.Desde(evento), cancellationToken);

    public async Task HandleAsync(IncapacidadRegistradaIntegrationEvent evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evento);

        string radicado = await RadicadoAsync(evento.TenantId, evento.CasoId, cancellationToken);
        await InsertarAsync(PayloadsWebhook.Desde(evento, radicado), cancellationToken);
    }

    public async Task HandleAsync(RegistroClinicoCreadoIntegrationEvent evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evento);

        string radicado = await RadicadoAsync(evento.TenantId, evento.CasoId, cancellationToken);
        await InsertarAsync(PayloadsWebhook.Desde(evento, radicado), cancellationToken);
    }

    public async Task HandleAsync(DictamenPclNotificadoIntegrationEvent evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evento);

        string radicado = await RadicadoAsync(evento.TenantId, evento.CasoId, cancellationToken);
        await InsertarAsync(PayloadsWebhook.Desde(evento, radicado), cancellationToken);
    }

    public async Task HandleAsync(AccesoEmergenciaRegistradoIntegrationEvent evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evento);

        string radicado = await RadicadoAsync(evento.TenantId, evento.CasoId, cancellationToken);
        await InsertarAsync(PayloadsWebhook.Desde(evento, radicado), cancellationToken);
    }

    public Task HandleAsync(CasoRemitidoIntegrationEvent evento, CancellationToken cancellationToken) =>
        InsertarAsync(PayloadsWebhook.Desde(evento), cancellationToken);

    public Task HandleAsync(RemisionRevocadaIntegrationEvent evento, CancellationToken cancellationToken) =>
        InsertarAsync(PayloadsWebhook.Desde(evento), cancellationToken);

    /// <summary>Devuelve cuántas entregas creó: 0 si no hay suscriptores o si el evento ya se había repartido.</summary>
    internal async Task<int> InsertarAsync(EventoPublico evento, CancellationToken ct)
    {
        // Npgsql writes timestamptz only from UTC offsets.
        DateTimeOffset ahora = time.GetUtcNow().ToUniversalTime();

        await using NpgsqlConnection conexion = await baseDatos.AbrirAsync(ct);
        await using NpgsqlCommand cmd = conexion.CreateCommand();
        cmd.CommandText = """
            INSERT INTO integracion.entregas_webhook
              (id, suscripcion_id, evento_id, tipo, payload, estado, intentos, primer_intento_en, proximo_intento_en)
            SELECT gen_random_uuid(), s.id, @evento, @tipo, CAST(@payload AS jsonb), 'PENDIENTE', 0, @ahora, @ahora
            FROM integracion.suscripciones_webhook s
            WHERE s.emisor = @emisor AND s.estado = 'ACTIVA' AND @tipo = ANY (s.eventos)
            ON CONFLICT (suscripcion_id, evento_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("evento", evento.EventoId);
        cmd.Parameters.AddWithValue("tipo", evento.Tipo);
        cmd.Parameters.AddWithValue("payload", evento.Payload);
        cmd.Parameters.AddWithValue("ahora", ahora);
        cmd.Parameters.AddWithValue("emisor", evento.Emisor);

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<string> RadicadoAsync(string tenantId, Guid casoId, CancellationToken ct)
    {
        using IDisposable _ = systemTenantScope.Entrar(new TenantId(tenantId));

        var id = new CasoMedicoId(casoId);
        return await readDb.CasosMedicos
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => c.Radicado)
            .FirstOrDefaultAsync(ct)
            // Committed before the event was: if it is not visible, something is wrong. The outbox retries.
            ?? throw new InvalidOperationException($"El caso {casoId} del evento no existe en {tenantId}.");
    }
}
