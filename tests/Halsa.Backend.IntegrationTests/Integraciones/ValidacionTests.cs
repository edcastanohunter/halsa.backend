using Halsa.Backend.Application.Abstractions.Integraciones;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Integraciones;
using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;
using static Halsa.Backend.IntegrationTests.Integraciones.IngestaSql;

namespace Halsa.Backend.IntegrationTests.Integraciones;

/// <summary>
/// SPEC 05, paso 8: la validación asíncrona, de la radicación al outbox, de <see cref="InProcessEventBus"/> a
/// <c>ValidarCasoRadicadoHandler</c>. Cada test radica en su propio tenant y luego vacía el outbox.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ValidacionTests(DatabaseFixture db)
{
    private static readonly DateTimeOffset Evento = new(2026, 10, 7, 14, 30, 0, TimeSpan.FromHours(-5));

    [Fact]
    public async Task Valid_ShouldBecomeValidado_WithItsEventAndASystemAuditRow()
    {
        const string tenant = "ARL_VAL_OK";
        RadicacionResponse radicacion = await RadicarValidoAsync(db, tenant, Furat("VAL-OK-1"));

        await ProcesarOutboxAsync(db.Services);

        (await EstadoAsync(radicacion.CasoId)).ShouldBe(("Validado", null, null));
        (await ContarAsync(db, """
            SELECT count(*) FROM outbox.outbox_messages
            WHERE type LIKE '%CasoValidadoIntegrationEvent' AND (content->>'casoId')::uuid = @caso
              AND content->>'numeroReporteOrigen' = 'VAL-OK-1'
            """, ("caso", radicacion.CasoId))).ShouldBe(1);
        (await ContarAsync(db, """
            SELECT count(*) FROM auditoria.accesos
            WHERE caso_medico_id = @caso AND accion = 'ACTUALIZAR' AND recurso_tipo = 'CASO'
              AND actor_tipo = 'sistema' AND actor_id = 'sistema' AND client_id = 'halsa-api' AND tenant_id = @tenant
            """, ("caso", radicacion.CasoId), ("tenant", tenant))).ShouldBe(1);
    }

    [Fact]
    public async Task SecondReport_OfTheSameWorker_ThreeHoursLater_ShouldBeADuplicate()
    {
        const string tenant = "ARL_VAL_DUPLICADO";
        RadicacionResponse primero = await RadicarValidoAsync(db, tenant, Furat("DUP-1", fechaHora: Evento));
        RadicacionResponse segundo = await RadicarValidoAsync(db, tenant, Furat("DUP-2", fechaHora: Evento.AddHours(3)));

        await ProcesarOutboxAsync(db.Services);

        (await EstadoAsync(primero.CasoId)).ShouldBe(("Validado", null, null));
        (await EstadoAsync(segundo.CasoId)).ShouldBe(("Rechazado", "Duplicado", primero.CasoId));
        (await ContarAsync(db, """
            SELECT count(*) FROM outbox.outbox_messages
            WHERE type LIKE '%CasoRechazadoIntegrationEvent' AND (content->>'casoId')::uuid = @caso
              AND content->>'motivo' = 'DUPLICADO' AND (content->>'duplicadoDe')::uuid = @primero
            """, ("caso", segundo.CasoId), ("primero", primero.CasoId))).ShouldBe(1);
    }

    [Fact]
    public async Task SameWorker_OutsideTheWindow_ShouldNotBeADuplicate()
    {
        const string tenant = "ARL_VAL_VENTANA";
        await RadicarValidoAsync(db, tenant, Furat("VENTANA-1", fechaHora: Evento.AddDays(-3)));
        RadicacionResponse segundo = await RadicarValidoAsync(db, tenant, Furat("VENTANA-2", fechaHora: Evento));

        await ProcesarOutboxAsync(db.Services);

        (await EstadoAsync(segundo.CasoId)).ShouldBe(("Validado", null, null));
    }

    [Fact]
    public async Task WorkerEndingIn000_ShouldBeRejectedAsNoAfiliado()
    {
        const string tenant = "ARL_VAL_NO_AFILIADO";
        RadicacionResponse radicacion = await RadicarValidoAsync(db, tenant, Furat("NOAF-1", trabajadorNumero: "1020304000"));

        await ProcesarOutboxAsync(db.Services);

        (await EstadoAsync(radicacion.CasoId)).ShouldBe(("Rechazado", "NoAfiliado", null));
    }

    [Fact]
    public async Task FailingGateway_ShouldLeaveTheMessagePending_AndTheCaseRadicado()
    {
        const string tenant = "ARL_VAL_CAIDO";
        RadicacionResponse radicacion = await RadicarValidoAsync(db, tenant, Furat("CAIDO-1"));

        await using (ServiceProvider conCoreCaido = db.CrearServicios(services =>
                     {
                         services.RemoveAll<IAfiliacionGateway>();
                         services.AddScoped<IAfiliacionGateway, CoreCaidoGateway>();
                     }))
        {
            await ProcesarOutboxAsync(conCoreCaido, maxAttempts: 2);
        }

        (await EstadoAsync(radicacion.CasoId)).ShouldBe(("Radicado", null, null));
        (await ContarAsync(db, """
            SELECT count(*) FROM outbox.outbox_messages
            WHERE type LIKE '%CasoRadicadoIntegrationEvent' AND (content->>'casoId')::uuid = @caso
              AND attempts > 0 AND processed_on_utc IS NULL AND error LIKE '%core de la ARL no responde%'
            """, ("caso", radicacion.CasoId))).ShouldBe(1);

        // Once the core answers again, the retry validates it.
        await ProcesarOutboxAsync(db.Services);
        (await EstadoAsync(radicacion.CasoId)).ShouldBe(("Validado", null, null));
    }

    [Fact]
    public async Task RedeliveredEvent_ShouldChangeNothing()
    {
        const string tenant = "ARL_VAL_REPETIDO";
        RadicacionResponse radicacion = await RadicarValidoAsync(db, tenant, Furat("REPETIDO-1"));
        await ProcesarOutboxAsync(db.Services);

        // At-least-once: the same message again.
        await EjecutarComoSuperusuarioAsync($"""
            UPDATE outbox.outbox_messages SET processed_on_utc = NULL
            WHERE type LIKE '%CasoRadicadoIntegrationEvent' AND content->>'casoId' = '{radicacion.CasoId}'
            """);
        await ProcesarOutboxAsync(db.Services);

        (await EstadoAsync(radicacion.CasoId)).ShouldBe(("Validado", null, null));
        (await ContarAsync(db, """
            SELECT count(*) FROM auditoria.accesos WHERE caso_medico_id = @caso AND accion = 'ACTUALIZAR'
            """, ("caso", radicacion.CasoId))).ShouldBe(1);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Drains the outbox as the hosted <see cref="OutboxProcessor"/> would, pass after pass until nothing is
    /// claimable. A message that keeps failing stops being claimable at <paramref name="maxAttempts"/>.
    /// </summary>
    private static async Task ProcesarOutboxAsync(IServiceProvider services, int maxAttempts = 5)
    {
        var processor = new OutboxProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OutboxOptions { BatchSize = 100, MaxAttempts = maxAttempts }),
            NullLogger<OutboxProcessor>.Instance);

        while (await processor.ProcessBatchAsync(CancellationToken.None) > 0)
        {
        }
    }

    private async Task<(string Estado, string? Motivo, Guid? DuplicadoDe)> EstadoAsync(Guid casoId)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.SuperuserConnectionString);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT estado, motivo_rechazo, duplicado_de FROM atel.casos_medicos WHERE id = @id";
        cmd.Parameters.AddWithValue("id", casoId);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();

        return (
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2));
    }

    private async Task EjecutarComoSuperusuarioAsync(string sql)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.SuperuserConnectionString);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The ARL core is down for <c>ARL_VAL_CAIDO</c>; every other tenant gets the dev stub.</summary>
    private sealed class CoreCaidoGateway : IAfiliacionGateway
    {
        private readonly DevAfiliacionGateway _dev = new();

        public Task<ResultadoAfiliacion> VerificarAsync(
            TenantId tenant, Identificacion trabajador, Identificacion empleador, DateTimeOffset fechaEvento, CancellationToken ct) =>
            tenant.Value == "ARL_VAL_CAIDO"
                ? throw new HttpRequestException("El core de la ARL no responde.")
                : _dev.VerificarAsync(tenant, trabajador, empleador, fechaEvento, ct);
    }
}
