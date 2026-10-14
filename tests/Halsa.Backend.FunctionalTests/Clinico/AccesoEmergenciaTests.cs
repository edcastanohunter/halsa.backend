using System.Net;
using System.Net.Http.Json;
using Halsa.Backend.FunctionalTests.Abstractions;
using Npgsql;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Clinico.DictamenesSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Clinico;

/// <summary>
/// SPEC 08, step 7: break-the-glass. A professional with <c>clinico:emergencia</c> and a <c>Halsa-Motivo-Acceso</c>
/// reads a case it is not assigned to; the read keeps the motivo and opens a review due in 72 h.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class AccesoEmergenciaTests(ApiFactory factory)
{
    /// <summary>25 characters.</summary>
    private const string Motivo = "Paciente en urgencias vit";

    private sealed record Revision(Guid Id, string TenantId, Guid AccesoId, string ActorId, string Motivo,
        DateTimeOffset OcurridoEn, DateTimeOffset VenceRevisionEn, DateTimeOffset? RevisadoEn);

    /// <summary><c>calificador.arla</c> as the dev realm seeds it: <c>clinico:emergencia</c> on top of its role.</summary>
    private HttpClient CalificadorConEmergencia(string? motivo)
    {
        Dictionary<string, object> claims = ConStepUp(CalificadorArlA(), "2", 1);
        claims["permissions"] = ((string[])claims["permissions"]).Append("clinico:emergencia").ToArray();
        HttpClient client = Client(factory, claims);
        if (motivo is not null)
        {
            client.DefaultRequestHeaders.Add("Halsa-Motivo-Acceso", motivo);
        }

        return client;
    }

    private async Task<List<Revision>> RevisionesAsync(Guid caso)
    {
        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, tenant_id, acceso_id, actor_id, motivo, ocurrido_en, vence_revision_en, revisado_en
            FROM auditoria.revisiones_emergencia WHERE caso_medico_id = @caso ORDER BY ocurrido_en
            """;
        cmd.Parameters.AddWithValue("caso", caso);

        var revisiones = new List<Revision>();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            revisiones.Add(new Revision(
                reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return revisiones;
    }

    /// <summary>The successful reads of the case, with their id and motivo.</summary>
    private async Task<List<(Guid Id, string? Motivo)>> LecturasExitosasAsync(Guid caso)
    {
        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, motivo FROM auditoria.accesos
            WHERE caso_medico_id = @caso AND accion = 'LEER' AND resultado = 'EXITO' ORDER BY ocurrido_en
            """;
        cmd.Parameters.AddWithValue("caso", caso);

        var filas = new List<(Guid, string?)>();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            filas.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return filas;
    }

    /// <summary>A case with a dictamen, registered by another calificador of ARL_A who is assigned.</summary>
    private async Task<Guid> CasoConDictamenDeOtroAsync()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);
        await AsignarAsync(factory, caso, ArlA, "otro-calificador");

        Dictionary<string, object> otro = ConStepUp(CalificadorArlA(), "2", 1);
        otro["sub"] = "otro-calificador";
        (await Client(factory, otro).PostAsJsonAsync(RutaDictamen(caso), Dictamen(), JsonOptions))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        return caso;
    }

    // ── Acceptance criteria ───────────────────────────────────────────────────

    [Fact]
    public async Task NotAssigned_WithClinicoEmergencia_AndA25CharMotivo_ShouldGet200_KeepTheMotivo_AndOpenAPendingReview()
    {
        Guid caso = await CasoConDictamenDeOtroAsync();

        HttpResponseMessage response = await CalificadorConEmergencia(Motivo).GetAsync(RutaDictamen(caso));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (Guid accesoId, string? motivo) = (await LecturasExitosasAsync(caso)).ShouldHaveSingleItem();
        motivo.ShouldBe(Motivo);

        Revision revision = (await RevisionesAsync(caso)).ShouldHaveSingleItem();
        revision.AccesoId.ShouldBe(accesoId);
        revision.TenantId.ShouldBe(ArlA);
        revision.ActorId.ShouldBe(CalificadorArlASub);
        revision.Motivo.ShouldBe(Motivo);
        revision.RevisadoEn.ShouldBeNull();
        (revision.VenceRevisionEn - revision.OcurridoEn).ShouldBe(TimeSpan.FromHours(72));
    }

    [Fact]
    public async Task TheSameAttempt_WithA10CharMotivo_ShouldGet403_AndOpenNoReview()
    {
        Guid caso = await CasoConDictamenDeOtroAsync();

        HttpResponseMessage response = await CalificadorConEmergencia("Diez carac").GetAsync(RutaDictamen(caso));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.MotivoEmergenciaInvalido");
        (await RevisionesAsync(caso)).ShouldBeEmpty();
    }

    [Fact]
    public async Task WithClinicoEmergencia_OnAnArlBCase_ShouldGet404()
    {
        Guid casoB = await SeedCasoAsync(factory, ArlB);

        HttpResponseMessage response = await CalificadorConEmergencia(Motivo).GetAsync(RutaDictamen(casoB));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RevisionesAsync(casoB)).ShouldBeEmpty();
    }

    // ── Grouping ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeveralReadsOfTheSameActorAndCase_WithinAnHour_ShouldShareOneReview()
    {
        Guid caso = await CasoConDictamenDeOtroAsync();
        await RegistrarConceptoAsync(caso, CalificadorArlA());
        HttpClient client = CalificadorConEmergencia(Motivo);

        (await client.GetAsync(RutaDictamen(caso))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync($"v1/casos/{caso}/registros-clinicos")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(RutaDictamen(caso))).StatusCode.ShouldBe(HttpStatusCode.OK);

        List<(Guid Id, string? Motivo)> lecturas = await LecturasExitosasAsync(caso);
        lecturas.Count.ShouldBe(3);
        lecturas.ShouldAllBe(l => l.Motivo == Motivo);
        (await RevisionesAsync(caso)).ShouldHaveSingleItem().AccesoId.ShouldBe(lecturas[0].Id);
    }

    [Fact]
    public async Task AReadMoreThanAnHourAfterThePendingReview_ShouldOpenANewOne()
    {
        Guid caso = await CasoConDictamenDeOtroAsync();
        HttpClient client = CalificadorConEmergencia(Motivo);
        (await client.GetAsync(RutaDictamen(caso))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Move the review back 2 h. The table protects its own columns, so the superuser skips the trigger.
        await using (var conn = new NpgsqlConnection(factory.SuperuserConnectionString))
        {
            await conn.OpenAsync();
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText = """
                SET session_replication_role = replica;
                UPDATE auditoria.revisiones_emergencia SET ocurrido_en = ocurrido_en - interval '2 hours'
                WHERE caso_medico_id = @caso;
                """;
            cmd.Parameters.AddWithValue("caso", caso);
            await cmd.ExecuteNonQueryAsync();
        }

        (await client.GetAsync(RutaDictamen(caso))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await RevisionesAsync(caso)).Count.ShouldBe(2);
    }

    // ── What the emergency does not open ──────────────────────────────────────

    [Fact]
    public async Task TheEmergency_ShouldNotLetAnUnassignedCalificadorWriteTheDictamen()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);

        HttpResponseMessage response = await CalificadorConEmergencia(Motivo).PostAsJsonAsync(RutaDictamen(caso), Dictamen(), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.NoAsignado");
        (await RevisionesAsync(caso)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAssignedCalificador_SendingAMotivo_ShouldReadNormally_WithoutAReview()
    {
        Guid caso = await CasoConDictamenDeOtroAsync();
        await AsignarAsync(factory, caso, ArlA, CalificadorArlASub);

        (await CalificadorConEmergencia(Motivo).GetAsync(RutaDictamen(caso))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await LecturasExitosasAsync(caso)).ShouldHaveSingleItem().Motivo.ShouldBeNull();
        (await RevisionesAsync(caso)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIpsDoctor_WithClinicoEmergencia_OnARemittedCase_ShouldOpenAReviewForTheCasesTenant()
    {
        Guid caso = await SeedCasoAsync(factory, ArlB, remitirA: Ips);
        await RegistrarConceptoAsync(caso, MedicoIps());
        Dictionary<string, object> medico = ConStepUp(MedicoIps(), "2");
        medico["permissions"] = ((string[])medico["permissions"]).Append("clinico:emergencia").ToArray();
        HttpClient client = Client(factory, medico);
        client.DefaultRequestHeaders.Add("Halsa-Motivo-Acceso", Motivo);

        (await client.GetAsync($"v1/casos/{caso}/registros-clinicos")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync($"v1/casos/{caso}/registros-clinicos")).StatusCode.ShouldBe(HttpStatusCode.OK);

        Revision revision = (await RevisionesAsync(caso)).ShouldHaveSingleItem();
        revision.TenantId.ShouldBe(ArlB);
        revision.ActorId.ShouldBe(MedicoIpsSub);
    }

    /// <summary>Writes a record through the SPEC 06 endpoint: writing only needs to see the case.</summary>
    private async Task RegistrarConceptoAsync(Guid caso, Dictionary<string, object> autor)
    {
        HttpResponseMessage response = await Client(factory, autor)
            .PostAsJsonAsync($"v1/casos/{caso}/registros-clinicos", Registro(), JsonOptions);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }
}
