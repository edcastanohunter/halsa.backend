using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Application.Integraciones.Conceptos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;
using static Halsa.Backend.IntegrationTests.Integraciones.IngestaSql;

namespace Halsa.Backend.IntegrationTests.Integraciones;

/// <summary>
/// SPEC 06, step 4: <see cref="RecibirConceptoMedicoCommand"/> through the mediator, as the integration of an IPS,
/// on <c>halsa_ingesta</c>. Each test seeds its own case and uses its own key.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ConceptosMedicosTests(DatabaseFixture db)
{
    /// <summary><c>int-ips-110011234501</c>, as the resolver leaves it.</summary>
    private static readonly FakeTenantContext IntegracionIps = new(ActorTipo.Integracion, null, new IpsId(Ips));

    private static byte[] Concepto(
        Guid casoId,
        string tipo = "CONCEPTO_MEDICO",
        DateTimeOffset? atendidoEn = null,
        Guid? corrigeA = null,
        string codigo = "S62.6",
        string? rethus = "RM-12345",
        string motivo = "Control postquirúrgico")
    {
        var cuerpo = new JsonObject
        {
            ["version"] = "1.0",
            ["casoId"] = casoId.ToString(),
            ["tipo"] = tipo,
            ["atendidoEn"] = (atendidoEn ?? DateTimeOffset.UtcNow.AddMinutes(-10))
                .ToOffset(TimeSpan.FromHours(-5)).ToString("O", CultureInfo.InvariantCulture),
            ["corrigeA"] = corrigeA?.ToString(),
            ["profesional"] = new JsonObject
            {
                ["identificacion"] = new JsonObject { ["tipo"] = "CC", ["numero"] = "79111222" },
                ["nombreCompleto"] = "Ana Ruiz",
                ["registroRethus"] = rethus,
                ["especialidad"] = "Ortopedia"
            },
            ["contenido"] = new JsonObject
            {
                ["motivo"] = motivo,
                ["anamnesis"] = "…",
                ["examenFisico"] = "…",
                ["analisis"] = "…",
                ["plan"] = "…",
                ["diagnosticos"] = new JsonArray(new JsonObject
                {
                    ["sistema"] = "CIE-10", ["codigo"] = codigo, ["descripcion"] = "Fractura de otro dedo de la mano"
                }),
                ["observaciones"] = null
            }
        };

        return Encoding.UTF8.GetBytes(cuerpo.ToJsonString());
    }

    private async Task<Result<RegistroClinicoCreadoResponse>> RecibirAsync(
        byte[] cuerpo, Guid? llave = null, FakeTenantContext? actor = null)
    {
        await using AsyncServiceScope scope = db.CreateScope(actor ?? IntegracionIps);

        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(
            new RecibirConceptoMedicoCommand((llave ?? Guid.NewGuid()).ToString(), cuerpo));
    }

    private async Task<RegistroClinicoCreadoResponse> RecibirValidoAsync(byte[] cuerpo, Guid? llave = null)
    {
        Result<RegistroClinicoCreadoResponse> result = await RecibirAsync(cuerpo, llave);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    private Task<long> RegistrosDelCasoAsync(Guid caso) =>
        ContarAsync(db, "SELECT count(*) FROM clinico.historia_clinica_detalle WHERE caso_medico_id = @caso", ("caso", caso));

    private Task<long> LlavesAsync(Guid llave) =>
        ContarAsync(db, "SELECT count(*) FROM integracion.solicitudes_idempotentes WHERE idempotency_key = @llave",
            ("llave", llave));

    // ── Success ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithVigenteRemision_OnAnArlBCase_ShouldRegister_WithTheCasesTenantAndDek()
    {
        Guid caso = await InsertCasoAsync(db, ArlB);
        await InsertRemisionAsync(db, caso, ArlB, Ips);
        Guid llave = Guid.NewGuid();

        RegistroClinicoCreadoResponse creado = await RecibirValidoAsync(Concepto(caso), llave);

        creado.CasoId.ShouldBe(caso);

        await using NpgsqlConnection conn = await OpenAsync(db.SuperuserConnectionString);
        await using (NpgsqlCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT h.tenant_id, h.tipo, h.profesional_sub, h.profesional_tipo_documento,
                       h.profesional_numero_documento, h.profesional_registro_rethus,
                       h.contenido_key_id = (SELECT key_id FROM clinico.llaves_tenant
                                             WHERE tenant_id = 'ARL_B' AND proposito = 'CONTENIDO' AND activa)
                FROM clinico.historia_clinica_detalle h WHERE h.id = @id
                """;
            cmd.Parameters.AddWithValue("id", creado.RegistroId);
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();

            (await reader.ReadAsync()).ShouldBeTrue();
            reader.GetString(0).ShouldBe(ArlB);
            reader.GetString(1).ShouldBe("ConceptoMedico");
            reader.GetString(2).ShouldBe(IntegracionIps.Sub);
            reader.GetString(3).ShouldBe("CC");
            reader.GetString(4).ShouldBe("79111222");
            reader.GetString(5).ShouldBe("RM-12345");
            reader.GetBoolean(6).ShouldBeTrue("contenido_key_id is not ARL_B's CONTENIDO DEK");
        }

        (await ContarAsync(db, """
            SELECT count(*) FROM integracion.solicitudes_idempotentes
            WHERE emisor = 'ips:110011234501' AND idempotency_key = @llave AND tenant_id = 'ARL_B'
            """, ("llave", llave))).ShouldBe(1);

        (await ContarAsync(db, """
            SELECT count(*) FROM auditoria.accesos
            WHERE caso_medico_id = @caso AND accion = 'CREAR' AND recurso_tipo = 'REGISTRO_CLINICO'
              AND recurso_id = @registro AND resultado = 'EXITO' AND tenant_id = 'ARL_B'
            """, ("caso", caso), ("registro", creado.RegistroId.ToString()))).ShouldBe(1);
    }

    [Fact]
    public async Task TheRegisteredContent_ShouldDecryptForTheCasesArl()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        await InsertRemisionAsync(db, caso, ArlA, Ips);

        RegistroClinicoCreadoResponse creado = await RecibirValidoAsync(Concepto(caso, motivo: "Valoración por la IPS"));

        Result<IReadOnlyList<Application.Clinico.Queries.GetRegistrosClinicos.RegistroClinicoResponse>> leidos =
            await SendAsync(db, FakeTenantContext.Arl(ArlA),
                new Application.Clinico.Queries.GetRegistrosClinicos.GetRegistrosClinicosQuery(
                    new Domain.Casos.CasoMedicoId(caso)));

        var leido = leidos.Value.ShouldHaveSingleItem();
        leido.Id.ShouldBe(creado.RegistroId);
        leido.Contenido.Motivo.ShouldBe("Valoración por la IPS");
        leido.Contenido.Diagnosticos.ShouldBe([new CodigoClinico("CIE-10", "S62.6")]);
    }

    // ── Visibility ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutRemision_ShouldFailWithCasoNoEncontrado_AndWriteNothing()
    {
        Guid caso = await InsertCasoAsync(db, ArlB);
        Guid llave = Guid.NewGuid();

        Result<RegistroClinicoCreadoResponse> result = await RecibirAsync(Concepto(caso), llave);

        result.Error.ShouldBe(ClinicoErrors.CasoNoEncontrado);
        (await RegistrosDelCasoAsync(caso)).ShouldBe(0);
        (await LlavesAsync(llave)).ShouldBe(0);
    }

    [Fact]
    public async Task AfterTheRemisionIsRevoked_ANewPost_ShouldFailWithCasoNoEncontrado()
    {
        Guid caso = await InsertCasoAsync(db, ArlB);
        Guid remision = await InsertRemisionAsync(db, caso, ArlB, Ips);
        await RecibirValidoAsync(Concepto(caso));

        await RevocarRemisionAsync(db, remision, ArlB);

        (await RecibirAsync(Concepto(caso))).Error.ShouldBe(ClinicoErrors.CasoNoEncontrado);
        (await RegistrosDelCasoAsync(caso)).ShouldBe(1);
    }

    [Fact]
    public async Task AnArlIntegration_ShouldFailWithActorNoAutorizado()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);

        Result<RegistroClinicoCreadoResponse> result = await RecibirAsync(Concepto(caso), actor: Integracion(ArlA));

        result.Error.ShouldBe(ClinicoErrors.ActorNoAutorizado);
    }

    [Fact]
    public async Task AnIpsPerson_ShouldFailWithActorNoAutorizado()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        await InsertRemisionAsync(db, caso, ArlA, Ips);

        Result<RegistroClinicoCreadoResponse> result = await RecibirAsync(Concepto(caso), actor: FakeTenantContext.Ips(Ips));

        result.Error.ShouldBe(ClinicoErrors.ActorNoAutorizado);
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SameKey_SameBody_ShouldReplayTheSameRegistro_AndLeaveASingleRow()
    {
        Guid caso = await InsertCasoAsync(db, ArlB);
        await InsertRemisionAsync(db, caso, ArlB, Ips);
        byte[] cuerpo = Concepto(caso);
        Guid llave = Guid.NewGuid();

        RegistroClinicoCreadoResponse primera = await RecibirValidoAsync(cuerpo, llave);
        RegistroClinicoCreadoResponse segunda = await RecibirValidoAsync(cuerpo, llave);

        segunda.ShouldBe(primera);
        (await RegistrosDelCasoAsync(caso)).ShouldBe(1);
    }

    [Fact]
    public async Task SameKey_AnotherBody_ShouldFailWithIdempotenciaConflicto()
    {
        Guid caso = await InsertCasoAsync(db, ArlB);
        await InsertRemisionAsync(db, caso, ArlB, Ips);
        Guid llave = Guid.NewGuid();

        await RecibirValidoAsync(Concepto(caso, motivo: "Primero"), llave);
        Result<RegistroClinicoCreadoResponse> result = await RecibirAsync(Concepto(caso, motivo: "Otro"), llave);

        result.Error.ShouldBe(IntegracionErrors.IdempotenciaConflicto);
        (await RegistrosDelCasoAsync(caso)).ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentPosts_WithTheSameKey_ShouldBothSucceed_WithTheSameRegistro()
    {
        Guid caso = await InsertCasoAsync(db, ArlB);
        await InsertRemisionAsync(db, caso, ArlB, Ips);
        byte[] cuerpo = Concepto(caso);
        Guid llave = Guid.NewGuid();

        Result<RegistroClinicoCreadoResponse>[] results = await Task.WhenAll(
            RecibirAsync(cuerpo, llave), RecibirAsync(cuerpo, llave));

        results.ShouldAllBe(r => r.IsSuccess);
        results.Select(r => r.Value.RegistroId).Distinct().ShouldHaveSingleItem();
        (await RegistrosDelCasoAsync(caso)).ShouldBe(1);
    }

    [Fact]
    public async Task TheSameKey_UsedByAnArlFuratAndByAnIpsConcepto_ShouldNotCollide()
    {
        Guid llave = Guid.NewGuid();
        await RadicarValidoAsync(db, ArlA, Furat($"K-{Guid.NewGuid():N}"[..30]), llave);

        Guid caso = await InsertCasoAsync(db, ArlA);
        await InsertRemisionAsync(db, caso, ArlA, Ips);
        await RecibirValidoAsync(Concepto(caso), llave);

        (await LlavesAsync(llave)).ShouldBe(2);
    }

    // ── Corrections ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CorrigeA_ShouldAddANewRecord_LeaveTheOriginal_AndAuditCorregir()
    {
        Guid caso = await InsertCasoAsync(db, ArlB);
        await InsertRemisionAsync(db, caso, ArlB, Ips);
        RegistroClinicoCreadoResponse original = await RecibirValidoAsync(Concepto(caso, motivo: "Con error"));

        RegistroClinicoCreadoResponse correccion = await RecibirValidoAsync(
            Concepto(caso, corrigeA: original.RegistroId, motivo: "Corregido"));

        (await ContarAsync(db, "SELECT count(*) FROM clinico.historia_clinica_detalle WHERE id = @id AND corrige_a = @original",
            ("id", correccion.RegistroId), ("original", original.RegistroId))).ShouldBe(1);
        (await ContarAsync(db, "SELECT count(*) FROM clinico.historia_clinica_detalle WHERE id = @id AND corrige_a IS NULL",
            ("id", original.RegistroId))).ShouldBe(1);
        (await ContarAsync(db, """
            SELECT count(*) FROM auditoria.accesos
            WHERE recurso_id = @id AND accion = 'CORREGIR' AND recurso_tipo = 'REGISTRO_CLINICO' AND tenant_id = 'ARL_B'
            """, ("id", correccion.RegistroId.ToString()))).ShouldBe(1);
    }

    [Fact]
    public async Task CorrigeA_OfARecordInAnotherCase_ShouldFailWithRegistroNoEncontrado()
    {
        Guid otro = await InsertCasoAsync(db, ArlA);
        await InsertRemisionAsync(db, otro, ArlA, Ips);
        RegistroClinicoCreadoResponse ajeno = await RecibirValidoAsync(Concepto(otro));

        Guid caso = await InsertCasoAsync(db, ArlA);
        await InsertRemisionAsync(db, caso, ArlA, Ips);

        Result<RegistroClinicoCreadoResponse> result = await RecibirAsync(Concepto(caso, corrigeA: ajeno.RegistroId));

        result.Error.ShouldBe(ClinicoErrors.RegistroNoEncontrado);
        (await RegistrosDelCasoAsync(caso)).ShouldBe(0);
    }

    [Fact]
    public async Task CorrigeA_OfARecordOfACaseNoLongerRemitted_ShouldFailWithRegistroNoEncontrado()
    {
        // The record exists in the same case, but only the ARL wrote it: without the remission the IPS cannot
        // know it exists. With the remission, the clinical RLS lets it see the case's history.
        Guid caso = await InsertCasoAsync(db, ArlA);
        Guid deLaArl = await RegistrarAsync(db, FakeTenantContext.Arl(ArlA), caso, Contenido("De la ARL"));
        await InsertRemisionAsync(db, caso, ArlA, Ips);

        (await RecibirAsync(Concepto(caso, corrigeA: deLaArl))).IsSuccess.ShouldBeTrue();
        (await RecibirAsync(Concepto(caso, corrigeA: Guid.CreateVersion7()))).Error
            .ShouldBe(ClinicoErrors.RegistroNoEncontrado);
    }

    // ── Race with a revocation ────────────────────────────────────────────────

    [Fact]
    public async Task AnInsert_AfterTheRemisionWasRevoked_ShouldBeRejectedByTheRls_AsFueraDeAlcance()
    {
        // The window of the spec's first risk: the case was read with a live remission, then it was revoked. The
        // WITH CHECK of the clinical policy evaluates the remission again at the insert.
        Guid caso = await InsertCasoAsync(db, ArlB);
        Guid remision = await InsertRemisionAsync(db, caso, ArlB, Ips);

        await using AsyncServiceScope scope = db.CreateScope(IntegracionIps);
        var ingesta = scope.ServiceProvider.GetRequiredService<Application.Abstractions.Integraciones.IIngestaDbContext>();

        await using var transaccion = await ingesta.BeginTransactionAsync(CancellationToken.None);
        (await ingesta.CasoVisibleAsync(new Domain.Casos.CasoMedicoId(caso), CancellationToken.None)).ShouldNotBeNull();

        await RevocarRemisionAsync(db, remision, ArlB);

        var cifrado = new CampoCifrado([1], new byte[12], new byte[16], "k");
        HistoriaClinicaDetalle registro = HistoriaClinicaDetalle.Registrar(
            HistoriaClinicaDetalleId.New(), new TenantId(ArlB), new Domain.Casos.CasoMedicoId(caso),
            TipoRegistroClinico.ConceptoMedico, new ProfesionalRef(IntegracionIps.Sub, "CC", "79111222", "RM-1"),
            DateTimeOffset.UtcNow.AddMinutes(-1), cifrado, null, null, null, DateTimeOffset.UtcNow).Value;
        ingesta.Add(registro);

        (await ingesta.GuardarAsync(CancellationToken.None))
            .ShouldBe(Application.Abstractions.Integraciones.ResultadoGuardado.FueraDeAlcance);
        await transaccion.RollbackAsync();

        (await RegistrosDelCasoAsync(caso)).ShouldBe(0);
    }

    // ── Rules ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnARejectedCase_ShouldFailWithCasoNoAdmiteRegistros()
    {
        Guid caso = await InsertCasoAsync(db, ArlB, estado: "Rechazado");
        await InsertRemisionAsync(db, caso, ArlB, Ips);

        (await RecibirAsync(Concepto(caso))).Error.ShouldBe(ClinicoErrors.CasoNoAdmiteRegistros);
    }

    [Fact]
    public async Task TwoDaysBeforeTheEvent_ShouldFailWithFechaAtencionAnteriorAlEvento()
    {
        DateTimeOffset fechaEvento = DateTimeOffset.UtcNow.AddDays(-7);
        Guid caso = await InsertCasoAsync(db, ArlB, fechaEvento);
        await InsertRemisionAsync(db, caso, ArlB, Ips);

        (await RecibirAsync(Concepto(caso, atendidoEn: fechaEvento.AddDays(-2)))).Error
            .ShouldBe(ClinicoErrors.FechaAtencionAnteriorAlEvento);
    }

    [Theory]
    [InlineData("DICTAMEN_PCL")]
    [InlineData("REPORTE_INICIAL")]
    public async Task ATypeOutsideTheThreeAdmitted_ShouldFailOnTipo(string tipo)
    {
        Result<RegistroClinicoCreadoResponse> result = await RecibirAsync(Concepto(Guid.CreateVersion7(), tipo));

        Rutas(result).ShouldBe(["tipo"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task WithoutRethus_ShouldFailOnProfesionalRegistroRethus(string? rethus)
    {
        Result<RegistroClinicoCreadoResponse> result = await RecibirAsync(Concepto(Guid.CreateVersion7(), rethus: rethus));

        Rutas(result).ShouldBe(["profesional.registroRethus"]);
    }

    [Theory]
    [InlineData("ABC.1")]
    [InlineData("S626")]
    public async Task AnInvalidCie10Code_ShouldFailOnTheDiagnosis(string codigo)
    {
        Result<RegistroClinicoCreadoResponse> result = await RecibirAsync(Concepto(Guid.CreateVersion7(), codigo: codigo));

        Rutas(result).ShouldBe(["contenido.diagnosticos[0].codigo"]);
    }

    [Fact]
    public async Task ANonUuidKey_ShouldFailWithIdempotencyKeyInvalida()
    {
        await using AsyncServiceScope scope = db.CreateScope(IntegracionIps);

        Result<RegistroClinicoCreadoResponse> result = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new RecibirConceptoMedicoCommand("no-es-uuid", Concepto(Guid.CreateVersion7())));

        result.Error.ShouldBe(IntegracionErrors.IdempotencyKeyInvalida);
    }

    private static string[] Rutas(Result result)
    {
        result.IsFailure.ShouldBeTrue();
        return [.. result.Error.ShouldBeOfType<ValidationError>().Errors.Cast<ErrorDeCampo>().Select(e => e.Ruta)];
    }
}
