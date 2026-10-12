using System.Text;
using System.Text.Json.Nodes;
using Halsa.Backend.Application.Abstractions.Integraciones;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Application.Integraciones.Canonico;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;
using static Halsa.Backend.IntegrationTests.Integraciones.IngestaSql;

namespace Halsa.Backend.IntegrationTests.Integraciones;

/// <summary>
/// SPEC 05, paso 6: la radicación transaccional como <c>halsa_ingesta</c>, la idempotencia y la concurrencia.
/// Cada test usa su propio tenant.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RadicacionTests(DatabaseFixture db)
{
    // ── Flujo completo ────────────────────────────────────────────────────────

    [Fact]
    public async Task Radicar_ShouldWriteEveryRow_InOneTransaction()
    {
        const string tenant = "ARL_ING_FLUJO";

        RadicacionResponse respuesta = await RadicarValidoAsync(db, tenant, Furat("FLUJO-1"));

        respuesta.Estado.ShouldBe(EstadosRadicacion.Radicado);
        respuesta.Radicado.ShouldBe($"ATEL-{tenant}-{DateTime.UtcNow.AddHours(-5).Year}-000001");
        respuesta.Plazos.ShouldBeEmpty();

        Dictionary<string, long> filas = await FilasDelTenantAsync(db, tenant);
        filas.ShouldBe(new Dictionary<string, long>
        {
            ["casos_medicos"] = 1,
            ["reportes_evento"] = 1,
            ["mensajes_entrantes"] = 1,
            ["reporte_inicial"] = 1,
            ["solicitudes"] = 1,
            ["solicitudes_idempotentes"] = 1,
            ["outbox"] = 1,
            ["auditoria"] = 2
        });

        // The same transaction: every row carries the same solicitud and case.
        (await ContarAsync(db, """
            SELECT count(*) FROM integracion.solicitudes s
            JOIN integracion.mensajes_entrantes m ON m.solicitud_id = s.id
            JOIN integracion.solicitudes_idempotentes i ON i.solicitud_id = s.id
            JOIN atel.reportes_evento r ON r.caso_medico_id = s.caso_medico_id
            JOIN clinico.historia_clinica_detalle h ON h.caso_medico_id = s.caso_medico_id
            WHERE s.id = @solicitud AND s.caso_medico_id = @caso
            """, ("solicitud", respuesta.SolicitudId), ("caso", respuesta.CasoId))).ShouldBe(1);

        (await ContarAsync(db, """
            SELECT count(*) FROM auditoria.accesos
            WHERE tenant_id = @tenant AND accion = 'CREAR' AND recurso_tipo IN ('CASO', 'REGISTRO_CLINICO')
              AND caso_medico_id = @caso AND actor_tipo = 'integracion'
            """, ("tenant", tenant), ("caso", respuesta.CasoId))).ShouldBe(2);
    }

    [Fact]
    public async Task Radicar_ShouldKeepTheClinicalFieldsOutOfTheAdministrativeData()
    {
        const string tenant = "ARL_ING_DATOS";
        RadicacionResponse respuesta = await RadicarValidoAsync(db, tenant, Furat("DATOS-1"));

        (await ContarAsync(db, """
            SELECT count(*) FROM atel.reportes_evento
            WHERE caso_medico_id = @caso
              AND NOT (datos->'evento' ? 'descripcion')
              AND NOT (datos->'evento' ? 'tiposLesion')
              AND NOT (datos->'evento' ? 'partesCuerpo')
              AND datos->'evento'->'agente'->>'codigo' = '300'
            """, ("caso", respuesta.CasoId))).ShouldBe(1);
    }

    [Fact]
    public async Task Mensaje_ShouldBeEncrypted_AndDecryptToTheRawBody()
    {
        const string tenant = "ARL_ING_MENSAJE";
        byte[] cuerpo = Furat("MENSAJE-1");
        RadicacionResponse respuesta = await RadicarValidoAsync(db, tenant, cuerpo);

        (await ContarAsync(db, """
            SELECT count(*) FROM integracion.mensajes_entrantes
            WHERE solicitud_id = @id AND position('andamio'::bytea in cuerpo_ct) = 0
              AND cuerpo_key_id = 'ARL_ING_MENSAJE:INTEGRACION:v1'
            """, ("id", respuesta.SolicitudId))).ShouldBe(1);

        CampoCifrado cifrado = await LeerMensajeAsync(respuesta.SolicitudId);
        IMensajeCipher cipher = db.Services.GetRequiredService<IMensajeCipher>();

        (await cipher.DescifrarAsync(new TenantId(tenant), respuesta.SolicitudId, cifrado, CancellationToken.None))
            .ShouldBe(cuerpo);
    }

    [Fact]
    public async Task Owner_ShouldNotModifyAnIncomingMessage()
    {
        const string tenant = "ARL_ING_INMUTABLE";
        RadicacionResponse respuesta = await RadicarValidoAsync(db, tenant, Furat("INMUTABLE-1"));

        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenant);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(() => ScalarAsync(conn,
            "UPDATE integracion.mensajes_entrantes SET tipo = tipo WHERE solicitud_id = @id", ("id", respuesta.SolicitudId)));

        ex.MessageText.ShouldContain("inmutable");
    }

    [Fact]
    public async Task Furep_ShouldCreateAnEnfermedadLaboral_WithTheBlindIndexOfItsDiagnosis()
    {
        const string tenant = "ARL_ING_FUREP";
        RadicacionResponse respuesta = await RadicarValidoAsync(db, tenant, Furep("FUREP-1"), tipo: TipoReporte.Furep);

        (await ContarAsync(db, "SELECT count(*) FROM atel.casos_medicos WHERE id = @id AND tipo_evento = 'EnfermedadLaboral'",
            ("id", respuesta.CasoId))).ShouldBe(1);

        byte[] esperado = await db.Services.GetRequiredService<Application.Abstractions.Clinico.IClinicalCipher>()
            .IndiceCiegoAsync(new TenantId(tenant), new CodigoClinico("CIE-10", "J45.0"), CancellationToken.None);

        (await ContarAsync(db, """
            SELECT count(*) FROM clinico.historia_clinica_detalle
            WHERE caso_medico_id = @caso AND tipo = 'ReporteInicial' AND diagnostico_indice_ciego = @indice
              AND profesional_registro_rethus = 'RM-55667'
            """, ("caso", respuesta.CasoId), ("indice", esperado))).ShouldBe(1);
    }

    [Fact]
    public async Task Furat_ReporteInicial_ShouldHaveNoRethus()
    {
        const string tenant = "ARL_ING_RETHUS";
        RadicacionResponse respuesta = await RadicarValidoAsync(db, tenant, Furat("RETHUS-1"));

        (await ContarAsync(db, """
            SELECT count(*) FROM clinico.historia_clinica_detalle
            WHERE caso_medico_id = @caso AND profesional_registro_rethus IS NULL
              AND profesional_numero_documento = '43123456'
            """, ("caso", respuesta.CasoId))).ShouldBe(1);
    }

    // ── Atomicidad ────────────────────────────────────────────────────────────

    [Fact]
    public async Task FailureInsertingTheClinicalRecord_ShouldLeaveNoRowAtAll()
    {
        const string tenant = "ARL_ING_FALLA";

        // A test-only constraint that rejects this tenant's clinical rows, and nobody else's.
        await EjecutarComoSuperusuarioAsync("""
            ALTER TABLE clinico.historia_clinica_detalle
                ADD CONSTRAINT ck_test_falla_ingesta CHECK (tenant_id <> 'ARL_ING_FALLA') NOT VALID
            """);

        try
        {
            await Should.ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(
                () => RadicarAsync(db, tenant, Furat("FALLA-1")));
        }
        finally
        {
            await EjecutarComoSuperusuarioAsync(
                "ALTER TABLE clinico.historia_clinica_detalle DROP CONSTRAINT ck_test_falla_ingesta");
        }

        (await FilasDelTenantAsync(db, tenant)).Values.ShouldAllBe(filas => filas == 0);
        (await ContarAsync(db, "SELECT count(*) FROM atel.secuencias_radicado WHERE tenant_id = @tenant", ("tenant", tenant)))
            .ShouldBe(0);
    }

    // ── Radicado ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sequences_ShouldBeIndependentPerTenant()
    {
        RadicacionResponse a1 = await RadicarValidoAsync(db, "ARL_ING_SEQ_A", Furat("SEQ-A-1"));
        RadicacionResponse a2 = await RadicarValidoAsync(db, "ARL_ING_SEQ_A", Furat("SEQ-A-2"));
        RadicacionResponse b1 = await RadicarValidoAsync(db, "ARL_ING_SEQ_B", Furat("SEQ-B-1"));

        a1.Radicado.ShouldEndWith("-000001");
        a2.Radicado.ShouldEndWith("-000002");
        b1.Radicado.ShouldEndWith("-000001");
        b1.Radicado.ShouldStartWith("ATEL-ARL_ING_SEQ_B-");
    }

    // ── Idempotencia ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SameKey_SameBody_ShouldReplayTheResponse_AndKeepOneCase()
    {
        const string tenant = "ARL_ING_REPITE";
        Guid llave = Guid.NewGuid();
        byte[] cuerpo = Furat("REPITE-1");

        RadicacionResponse primera = await RadicarValidoAsync(db, tenant, cuerpo, llave);
        RadicacionResponse segunda = await RadicarValidoAsync(db, tenant, cuerpo, llave);

        segunda.SolicitudId.ShouldBe(primera.SolicitudId);
        segunda.Radicado.ShouldBe(primera.Radicado);
        segunda.CasoId.ShouldBe(primera.CasoId);
        (await FilasDelTenantAsync(db, tenant))["casos_medicos"].ShouldBe(1);
    }

    [Fact]
    public async Task SameKey_AnotherBody_ShouldBeAConflict()
    {
        const string tenant = "ARL_ING_CONFLICTO";
        Guid llave = Guid.NewGuid();

        await RadicarValidoAsync(db, tenant, Furat("CONFLICTO-1"), llave);
        Result<RadicacionResponse> segunda = await RadicarAsync(db, tenant, Furat("CONFLICTO-2"), llave);

        segunda.Error.ShouldBe(IntegracionErrors.IdempotenciaConflicto);
        (await FilasDelTenantAsync(db, tenant))["casos_medicos"].ShouldBe(1);
    }

    [Fact]
    public async Task SameKey_FromAnotherTenant_ShouldNotCollide()
    {
        Guid llave = Guid.NewGuid();

        RadicacionResponse a = await RadicarValidoAsync(db, "ARL_ING_EMISOR_A", Furat("EMISOR-1"), llave);
        RadicacionResponse b = await RadicarValidoAsync(db, "ARL_ING_EMISOR_B", Furat("EMISOR-1"), llave);

        b.SolicitudId.ShouldNotBe(a.SolicitudId);
    }

    [Fact]
    public async Task ExpiredKey_ShouldBeClaimedAgain()
    {
        const string tenant = "ARL_ING_VENCIDA";
        Guid llave = Guid.NewGuid();

        await RadicarValidoAsync(db, tenant, Furat("VENCIDA-1"), llave);
        await EjecutarComoSuperusuarioAsync(
            $"UPDATE integracion.solicitudes_idempotentes SET expira_en = now() - interval '1 hour' WHERE idempotency_key = '{llave}'");

        // Another body with the expired key is a new radication, not a conflict.
        RadicacionResponse nueva = await RadicarValidoAsync(db, tenant, Furat("VENCIDA-2"), llave);

        nueva.Radicado.ShouldEndWith("-000002");
        (await FilasDelTenantAsync(db, tenant))["solicitudes_idempotentes"].ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentRequests_WithTheSameKey_ShouldLeaveOneCase_AndAnswerTheSame()
    {
        const string tenant = "ARL_ING_CONCURRENTE";
        Guid llave = Guid.NewGuid();
        byte[] cuerpo = Furat("CONCURRENTE-1");

        Result<RadicacionResponse>[] resultados = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => Task.Run(() => RadicarAsync(db, tenant, cuerpo, llave))));

        resultados.ShouldAllBe(r => r.IsSuccess);
        resultados.Select(r => r.Value.Radicado).Distinct().ShouldHaveSingleItem();
        resultados.Select(r => r.Value.SolicitudId).Distinct().ShouldHaveSingleItem();
        (await FilasDelTenantAsync(db, tenant))["casos_medicos"].ShouldBe(1);
    }

    [Fact]
    public async Task AnotherKey_WithTheSameReportNumber_ShouldBeReporteYaRadicado()
    {
        const string tenant = "ARL_ING_YA_RADICADO";

        RadicacionResponse primera = await RadicarValidoAsync(db, tenant, Furat("YA-1"));
        Result<RadicacionResponse> segunda = await RadicarAsync(db, tenant, Furat("YA-1", trabajadorNumero: "999888777"));

        segunda.Error.Code.ShouldBe(IntegracionErrors.ReporteYaRadicadoCodigo);
        segunda.Error.ShouldBeOfType<ReporteYaRadicadoError>().Radicado.ShouldBe(primera.Radicado);
        (await FilasDelTenantAsync(db, tenant)).ShouldContainKeyAndValue("casos_medicos", 1);
        (await FilasDelTenantAsync(db, tenant)).ShouldContainKeyAndValue("solicitudes_idempotentes", 1);
    }

    // ── Actor y llave ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ArlAnalyst_ShouldNotRadicate()
    {
        await using AsyncServiceScope scope = db.CreateScope(FakeTenantContext.Arl("ARL_A"));

        Result<RadicacionResponse> result = await scope.ServiceProvider.GetRequiredService<Mediation.ISender>()
            .Send(new Application.Integraciones.Commands.Radicar.RadicarReporteCommand(
                TipoReporte.Furat, Guid.NewGuid().ToString(), Furat("ANALISTA-1"), null, "c"));

        result.Error.ShouldBe(IntegracionErrors.ActorNoAutorizado);
    }

    [Fact]
    public async Task InvalidKey_ShouldBeRejected()
    {
        await using AsyncServiceScope scope = db.CreateScope(Integracion("ARL_ING_LLAVE"));

        Result<RadicacionResponse> result = await scope.ServiceProvider.GetRequiredService<Mediation.ISender>()
            .Send(new Application.Integraciones.Commands.Radicar.RadicarReporteCommand(
                TipoReporte.Furat, "no-es-un-uuid", Furat("LLAVE-1"), null, "c"));

        result.Error.ShouldBe(IntegracionErrors.IdempotencyKeyInvalida);
    }

    [Fact]
    public async Task InvalidBody_ShouldNotWriteAnything()
    {
        const string tenant = "ARL_ING_INVALIDO";
        JsonObject cuerpo = Tests.Shared.EjemplosContrato.Objeto(Tests.Shared.EjemplosContrato.FuratCaidaAltura);
        cuerpo["evento"]!["fechaHora"] = DateTimeOffset.UtcNow.AddDays(1).ToString("O");

        Result<RadicacionResponse> result = await RadicarAsync(db, tenant, Encoding.UTF8.GetBytes(cuerpo.ToJsonString()));

        result.Error.ShouldBeOfType<ValidationError>().Errors.Cast<ErrorDeCampo>().Single().Ruta.ShouldBe("evento.fechaHora");
        (await FilasDelTenantAsync(db, tenant)).Values.ShouldAllBe(filas => filas == 0);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task EjecutarComoSuperusuarioAsync(string sql)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.SuperuserConnectionString);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<CampoCifrado> LeerMensajeAsync(Guid solicitudId)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.SuperuserConnectionString);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT cuerpo_ct, cuerpo_nonce, cuerpo_tag, cuerpo_key_id
            FROM integracion.mensajes_entrantes WHERE solicitud_id = @id
            """;
        cmd.Parameters.AddWithValue("id", solicitudId);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();

        return new CampoCifrado(
            reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1),
            reader.GetFieldValue<byte[]>(2), reader.GetString(3));
    }
}
