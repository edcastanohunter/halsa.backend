using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using Npgsql;

namespace Halsa.Backend.FunctionalTests.Casos;

/// <summary>
/// Acceptance criteria of SPEC 02 for <c>/v1/casos</c>. The API runs as <c>halsa_app</c>, so both
/// barriers (EF query filter and RLS) are active. Every test seeds its own cases as
/// <c>halsa_owner</c> and asserts only on them, so tests are independent of each other.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class CasosEndpointsTests(ApiFactory factory)
{
    private const string ArlA = "ARL_A";
    private const string ArlB = "ARL_B";
    private const string Ips = "110011234501";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ── Lectura ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListCasos_AsArlA_ShouldNotIncludeArlBCases()
    {
        Guid casoA = await SeedCasoAsync(ArlA);
        Guid casoB = await SeedCasoAsync(ArlB);

        List<Guid> ids = await ListIdsAsync(ArlClient(ArlA));

        ids.ShouldContain(casoA);
        ids.ShouldNotContain(casoB);
    }

    [Fact]
    public async Task GetCasoById_OfArlBCase_AsArlA_ShouldReturn404()
    {
        Guid casoB = await SeedCasoAsync(ArlB);

        HttpResponseMessage response = await ArlClient(ArlA).GetAsync($"v1/casos/{casoB}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListCasos_AsIps_ShouldReturnOnlyCasesWithVigenteRemision_FromBothTenants()
    {
        Guid remitidoA = await SeedCasoAsync(ArlA, remitirA: Ips);
        Guid remitidoB = await SeedCasoAsync(ArlB, remitirA: Ips);
        Guid sinRemitir = await SeedCasoAsync(ArlA);

        List<Guid> ids = await ListIdsAsync(IpsClient());

        ids.ShouldContain(remitidoA);
        ids.ShouldContain(remitidoB);
        ids.ShouldNotContain(sinRemitir);
    }

    [Fact]
    public async Task GetCasoById_ShouldReturnDetalle_WithRemisionesVigentesAndEquipo()
    {
        Guid caso = await SeedCasoAsync(ArlA, remitirA: Ips);
        HttpClient client = ArlClient(ArlA);
        (await client.PostAsJsonAsync($"v1/casos/{caso}/asignaciones", new { sub = "sub-equipo" }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HttpResponseMessage response = await client.GetAsync($"v1/casos/{caso}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions))!;
        body["resumen"]!["id"]!.GetValue<Guid>().ShouldBe(caso);
        body["resumen"]!["estado"]!.GetValue<string>().ShouldBe("Radicado");
        body["remisionesVigentes"]!.AsArray().Single()!["ipsId"]!.GetValue<string>().ShouldBe(Ips);
        body["equipoAsignado"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["sub-equipo"]);
    }

    [Fact]
    public async Task ListCasos_ShouldReturn401_WhenUnauthenticated()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "v1/casos");
        request.Headers.Add("X-Unauthenticated", "true");

        HttpResponseMessage response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ── Remisiones ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemitirCaso_TwiceToSameIps_ShouldReturn201ThenConflict()
    {
        Guid caso = await SeedCasoAsync(ArlA);
        HttpClient client = ArlClient(ArlA);

        HttpResponseMessage primera = await client.PostAsJsonAsync($"v1/casos/{caso}/remisiones", new { ipsId = Ips });
        HttpResponseMessage segunda = await client.PostAsJsonAsync($"v1/casos/{caso}/remisiones", new { ipsId = Ips });

        primera.StatusCode.ShouldBe(HttpStatusCode.Created);
        segunda.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(segunda)).ShouldBe("Caso.RemisionDuplicada");
    }

    [Fact]
    public async Task RemitirCaso_AsIpsWithCasosWrite_ShouldReturn403()
    {
        Guid caso = await SeedCasoAsync(ArlA, remitirA: Ips);

        HttpResponseMessage response = await IpsClient()
            .PostAsJsonAsync($"v1/casos/{caso}/remisiones", new { ipsId = "110011234502" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("Caso.ActorNoAutorizado");
    }

    [Fact]
    public async Task RemitirCaso_WithInvalidIpsId_ShouldReturn400()
    {
        Guid caso = await SeedCasoAsync(ArlA);

        HttpResponseMessage response = await ArlClient(ArlA)
            .PostAsJsonAsync($"v1/casos/{caso}/remisiones", new { ipsId = "123" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RemitirCaso_OfInvisibleCase_ShouldReturn404()
    {
        Guid casoB = await SeedCasoAsync(ArlB);

        HttpResponseMessage response = await ArlClient(ArlA)
            .PostAsJsonAsync($"v1/casos/{casoB}/remisiones", new { ipsId = Ips });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RevocarRemision_ShouldReturn204_ThenConflictOnSecondRevocation()
    {
        Guid caso = await SeedCasoAsync(ArlA);
        HttpClient client = ArlClient(ArlA);
        HttpResponseMessage remitir = await client.PostAsJsonAsync($"v1/casos/{caso}/remisiones", new { ipsId = Ips });
        Guid remisionId = (await remitir.Content.ReadFromJsonAsync<JsonObject>(JsonOptions))!["remisionId"]!.GetValue<Guid>();

        HttpResponseMessage primera = await client.PostAsync($"v1/casos/{caso}/remisiones/{remisionId}/revocar", null);
        HttpResponseMessage segunda = await client.PostAsync($"v1/casos/{caso}/remisiones/{remisionId}/revocar", null);

        primera.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        segunda.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(segunda)).ShouldBe("Caso.RemisionYaRevocada");

        // Revoked: the IPS no longer sees the case.
        (await ListIdsAsync(IpsClient())).ShouldNotContain(caso);
    }

    // ── Asignaciones ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AsignarCaso_Twice_ShouldReturn204BothTimes_AndKeepOneRow()
    {
        Guid caso = await SeedCasoAsync(ArlA);
        HttpClient client = ArlClient(ArlA);

        HttpResponseMessage primera = await client.PostAsJsonAsync($"v1/casos/{caso}/asignaciones", new { sub = "sub-idem" });
        HttpResponseMessage segunda = await client.PostAsJsonAsync($"v1/casos/{caso}/asignaciones", new { sub = "sub-idem" });

        primera.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        segunda.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await CountAsignacionesAsync(caso, ArlA, "sub-idem")).ShouldBe(1);
    }

    [Fact]
    public async Task DesasignarCaso_ShouldReturn204_Then404WhenNotAssigned()
    {
        Guid caso = await SeedCasoAsync(ArlA);
        HttpClient client = ArlClient(ArlA);
        (await client.PostAsJsonAsync($"v1/casos/{caso}/asignaciones", new { sub = "sub-fuera" }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HttpResponseMessage primera = await client.DeleteAsync($"v1/casos/{caso}/asignaciones/sub-fuera");
        HttpResponseMessage segunda = await client.DeleteAsync($"v1/casos/{caso}/asignaciones/sub-fuera");

        primera.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        segunda.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(segunda)).ShouldBe("Caso.AsignacionNoEncontrada");
    }

    // ── Clients ───────────────────────────────────────────────────────────────

    private HttpClient ArlClient(string tenantId) =>
        ClientWithClaims(new
        {
            sub = TestAuthenticationHandler.TestUserId.ToString(),
            iss = TestAuthenticationHandler.DefaultIssuer,
            actor_tipo = "arl",
            tenant_id = tenantId,
            permissions = new[] { "casos:read", "casos:write" }
        });

    /// <summary>An IPS actor that (wrongly) holds <c>casos:write</c>: the handler must still refuse writes.</summary>
    private HttpClient IpsClient() =>
        ClientWithClaims(new
        {
            sub = "01999000-0000-7000-8000-000000000c01",
            iss = TestAuthenticationHandler.DefaultIssuer,
            actor_tipo = "ips",
            ips_id = Ips,
            permissions = new[] { "casos:read", "casos:write" }
        });

    private HttpClient ClientWithClaims(object claims) =>
        factory.CreateClientWithHeaders(new Dictionary<string, string>
        {
            ["X-Test-Claims"] = JsonSerializer.Serialize(claims)
        });

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<List<Guid>> ListIdsAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync("v1/casos?page=1&pageSize=1000");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions))!;
        return body["items"]!.AsArray().Select(item => item!["id"]!.GetValue<Guid>()).ToList();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        JsonObject? body = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions);
        return body?["errorCode"]?.GetValue<string>();
    }

    /// <summary>
    /// Seeds a case as <c>halsa_owner</c>. RLS is forced, so the owner sets the tenant first.
    /// </summary>
    private async Task<Guid> SeedCasoAsync(string tenantId, string? remitirA = null)
    {
        Guid id = Guid.CreateVersion7();

        await using NpgsqlConnection conn = await OpenOwnerAsync(tenantId);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO atel.casos_medicos
              (id, tenant_id, radicado, tipo_evento, fecha_evento,
               trabajador_tipo, trabajador_numero, empleador_tipo, empleador_numero,
               municipio_divipola, estado, es_grave_o_mortal, radicado_en)
            VALUES
              (@id, @tid, @rad, 'AccidenteTrabajo', now() - interval '1 day', 'CC', '123456', 'NIT', '9001',
               '11001', 'Radicado', false, now());
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("rad", $"FT-{id:N}");
        await cmd.ExecuteNonQueryAsync();

        if (remitirA is not null)
        {
            await using NpgsqlCommand remision = conn.CreateCommand();
            remision.CommandText = """
                INSERT INTO atel.remisiones (id, caso_medico_id, tenant_id, ips_id, remitido_en, remitido_por)
                VALUES (@rid, @id, @tid, @ips, now(), 'sub-seed');
                """;
            remision.Parameters.AddWithValue("rid", Guid.CreateVersion7());
            remision.Parameters.AddWithValue("id", id);
            remision.Parameters.AddWithValue("tid", tenantId);
            remision.Parameters.AddWithValue("ips", remitirA);
            await remision.ExecuteNonQueryAsync();
        }

        return id;
    }

    private async Task<long> CountAsignacionesAsync(Guid casoId, string tenantId, string sub)
    {
        await using NpgsqlConnection conn = await OpenOwnerAsync(tenantId);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM atel.asignaciones WHERE caso_medico_id = @id AND sub = @sub";
        cmd.Parameters.AddWithValue("id", casoId);
        cmd.Parameters.AddWithValue("sub", sub);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<NpgsqlConnection> OpenOwnerAsync(string tenantId)
    {
        var conn = new NpgsqlConnection(factory.OwnerConnectionString);
        await conn.OpenAsync();

        await using NpgsqlCommand set = conn.CreateCommand();
        set.CommandText = "SELECT set_config('app.tenant_id', @tid, false)";
        set.Parameters.AddWithValue("tid", tenantId);
        await set.ExecuteNonQueryAsync();

        return conn;
    }
}
