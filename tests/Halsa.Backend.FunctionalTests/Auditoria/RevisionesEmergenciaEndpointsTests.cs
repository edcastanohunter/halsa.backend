using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using Halsa.Backend.FunctionalTests.Clinico;
using Npgsql;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Auditoria;

/// <summary>
/// SPEC 08, step 8: <c>GET /v1/auditoria/revisiones-emergencia</c> and <c>POST …/{id}/cerrar</c>, for the privacy
/// officer of the case's tenant with <c>auditoria:revisar</c>.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class RevisionesEmergenciaEndpointsTests(ApiFactory factory)
{
    private const string Ruta = "v1/auditoria/revisiones-emergencia";
    private const string Motivo = "Paciente en urgencias vit";

    private static string RutaCerrar(Guid id) => $"{Ruta}/{id}/cerrar";

    /// <summary><c>privacidad.arla</c> as the realm seeds it: <c>oficial-privacidad</c>, which now has <c>auditoria:revisar</c>.</summary>
    private HttpClient Privacidad(string tenant = ArlA, params string[] permisos) => Client(factory, new Dictionary<string, object>
    {
        ["sub"] = "01999000-0000-7000-8000-000000000a03",
        ["iss"] = TestAuthenticationHandler.DefaultIssuer,
        ["azp"] = "halsa-backoffice",
        ["actor_tipo"] = "arl",
        ["tenant_id"] = tenant,
        ["permissions"] = permisos.Length > 0 ? permisos : new[] { "auditoria:read", "auditoria:export", "auditoria:revisar" }
    });

    /// <summary>A pending review of a new case of <paramref name="tenant"/>, as the API would open it.</summary>
    private async Task<(Guid Id, Guid Caso)> RevisionPendienteAsync(string tenant = ArlA)
    {
        Guid caso = await SeedCasoAsync(factory, tenant);
        Guid id = Guid.CreateVersion7();

        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO auditoria.revisiones_emergencia
              (id, tenant_id, acceso_id, actor_id, caso_medico_id, motivo, ocurrido_en, vence_revision_en)
            VALUES (@id, @tid, gen_random_uuid(), 'sub-emergencia', @caso, @motivo, now(), now() + interval '72 hours')
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", tenant);
        cmd.Parameters.AddWithValue("caso", caso);
        cmd.Parameters.AddWithValue("motivo", Motivo);
        await cmd.ExecuteNonQueryAsync();

        return (id, caso);
    }

    private static async Task<List<Guid>> IdsAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonObject body = await BodyAsync(response);
        return [.. body["items"]!.AsArray().Select(i => i!["id"]!.GetValue<Guid>())];
    }

    private static JsonObject Cierre(string conclusion = "JUSTIFICADO", string? observaciones = "Urgencia vital confirmada.") =>
        new() { ["conclusion"] = conclusion, ["observaciones"] = observaciones };

    // ── Acceptance criterion ──────────────────────────────────────────────────

    [Fact]
    public async Task Privacidad_ShouldListTheReviewOpenedByAnEmergencyRead_CloseItJustificado_AndGet409OnASecondClose()
    {
        // The emergency read of step 7: calificador.arla, not assigned, with clinico:emergencia and a motivo.
        Guid caso = await SeedCasoAsync(factory, ArlA);
        Dictionary<string, object> calificador = ConStepUp(CalificadorArlA(), "2");
        calificador["permissions"] = ((string[])calificador["permissions"]).Append("clinico:emergencia").ToArray();
        HttpClient emergencia = Client(factory, calificador);
        emergencia.DefaultRequestHeaders.Add("Halsa-Motivo-Acceso", Motivo);
        (await emergencia.GetAsync($"v1/casos/{caso}/registros-clinicos")).StatusCode.ShouldBe(HttpStatusCode.OK);

        HttpClient privacidad = Privacidad();
        JsonObject pendiente = (await BodyAsync(await privacidad.GetAsync($"{Ruta}?estado=PENDIENTE&pageSize=200")))["items"]!
            .AsArray().Select(i => i!.AsObject()).Single(i => i["casoMedicoId"]!.GetValue<Guid>() == caso);
        Guid id = pendiente["id"]!.GetValue<Guid>();
        pendiente["estado"]!.GetValue<string>().ShouldBe("PENDIENTE");
        pendiente["motivo"]!.GetValue<string>().ShouldBe(Motivo);
        pendiente["actorId"]!.GetValue<string>().ShouldBe(CalificadorArlASub);

        HttpResponseMessage primero = await privacidad.PostAsJsonAsync(RutaCerrar(id), Cierre(), JsonOptions);
        HttpResponseMessage segundo = await privacidad.PostAsJsonAsync(RutaCerrar(id), Cierre("INJUSTIFICADO"), JsonOptions);

        primero.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        segundo.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(segundo)).ShouldBe("Auditoria.RevisionYaCerrada");

        JsonObject cerrada = (await BodyAsync(await privacidad.GetAsync($"{Ruta}?estado=CERRADA&pageSize=200")))["items"]!
            .AsArray().Select(i => i!.AsObject()).Single(i => i["id"]!.GetValue<Guid>() == id);
        cerrada["conclusion"]!.GetValue<string>().ShouldBe("JUSTIFICADO");
        cerrada["revisadoPor"]!.GetValue<string>().ShouldBe("01999000-0000-7000-8000-000000000a03");
        cerrada["observaciones"]!.GetValue<string>().ShouldBe("Urgencia vital confirmada.");

        // The close is audited with the case, in the same transaction.
        (await AuditoriaAsync(factory, caso)).ShouldContain(
            new AuditoriaFila("ACTUALIZAR", "AUDITORIA", id.ToString(), "EXITO", ArlA, "halsa-backoffice"));
    }

    // ── Listing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheFilter_ShouldSeparatePendingFromClosed()
    {
        (Guid pendiente, _) = await RevisionPendienteAsync();
        (Guid cerrada, _) = await RevisionPendienteAsync();
        HttpClient privacidad = Privacidad();
        (await privacidad.PostAsJsonAsync(RutaCerrar(cerrada), Cierre(), JsonOptions)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        List<Guid> pendientes = await IdsAsync(await privacidad.GetAsync($"{Ruta}?estado=PENDIENTE&pageSize=200"));
        List<Guid> cerradas = await IdsAsync(await privacidad.GetAsync($"{Ruta}?estado=CERRADA&pageSize=200"));
        List<Guid> todas = await IdsAsync(await privacidad.GetAsync($"{Ruta}?pageSize=200"));

        pendientes.ShouldContain(pendiente);
        pendientes.ShouldNotContain(cerrada);
        cerradas.ShouldContain(cerrada);
        cerradas.ShouldNotContain(pendiente);
        todas.ShouldContain(pendiente);
        todas.ShouldContain(cerrada);
    }

    [Fact]
    public async Task AnUnknownEstado_ShouldAnswer400()
    {
        HttpResponseMessage response = await Privacidad().GetAsync($"{Ruta}?estado=ABIERTA");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Auditoria.EstadoInvalido");
    }

    // ── Tenant and permission ─────────────────────────────────────────────────

    [Fact]
    public async Task AnotherTenantsOfficer_ShouldNeitherSeeNorCloseIt()
    {
        (Guid id, _) = await RevisionPendienteAsync(ArlA);
        HttpClient privacidadB = Privacidad(ArlB);

        (await IdsAsync(await privacidadB.GetAsync($"{Ruta}?pageSize=200"))).ShouldNotContain(id);

        HttpResponseMessage cierre = await privacidadB.PostAsJsonAsync(RutaCerrar(id), Cierre(), JsonOptions);
        cierre.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(cierre)).ShouldBe("Auditoria.RevisionNoEncontrada");
    }

    [Fact]
    public async Task AuditoriaRead_WithoutAuditoriaRevisar_ShouldGet403()
    {
        (Guid id, _) = await RevisionPendienteAsync();
        HttpClient soloLectura = Privacidad(ArlA, "auditoria:read", "auditoria:export");

        (await soloLectura.GetAsync(Ruta)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await soloLectura.PostAsJsonAsync(RutaCerrar(id), Cierre(), JsonOptions)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnIpsActorWithAuditoriaRevisar_ShouldGet403()
    {
        HttpClient ips = Client(factory, new Dictionary<string, object>
        {
            ["sub"] = "sub-ips",
            ["iss"] = TestAuthenticationHandler.DefaultIssuer,
            ["actor_tipo"] = "ips",
            ["ips_id"] = Ips,
            ["permissions"] = new[] { "auditoria:revisar" }
        });

        HttpResponseMessage response = await ips.GetAsync(Ruta);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("Auditoria.ActorNoAutorizado");
    }

    // ── Closing ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("TAL_VEZ")]
    [InlineData("")]
    public async Task AnUnknownConclusion_ShouldAnswer400_AndLeaveItPending(string conclusion)
    {
        (Guid id, _) = await RevisionPendienteAsync();
        HttpClient privacidad = Privacidad();

        HttpResponseMessage response = await privacidad.PostAsJsonAsync(RutaCerrar(id), Cierre(conclusion), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).ShouldNotBeNull();
        (await IdsAsync(await privacidad.GetAsync($"{Ruta}?estado=PENDIENTE&pageSize=200"))).ShouldContain(id);
    }

    [Fact]
    public async Task ClosingWithoutObservaciones_ShouldWork()
    {
        (Guid id, _) = await RevisionPendienteAsync();

        HttpResponseMessage response = await Privacidad().PostAsJsonAsync(RutaCerrar(id), Cierre("INJUSTIFICADO", null), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task AnUnknownReview_ShouldAnswer404()
    {
        HttpResponseMessage response = await Privacidad().PostAsJsonAsync(RutaCerrar(Guid.CreateVersion7()), Cierre(), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
