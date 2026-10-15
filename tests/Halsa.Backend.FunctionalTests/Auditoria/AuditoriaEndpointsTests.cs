using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.FunctionalTests.Abstractions;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Halsa.Backend.FunctionalTests.Auditoria;

/// <summary>
/// Acceptance criteria of SPEC 04 over HTTP: the case detail is audited and fails closed, the list is
/// not audited, policy denials on <c>/v1/casos</c> are recorded, and the privacy officer reads only
/// the audit of their own tenant. Assertions on raw rows read as the superuser, which bypasses RLS.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class AuditoriaEndpointsTests(ApiFactory factory)
{
    private const string ArlA = "ARL_A";
    private const string ArlB = "ARL_B";
    private const string Ips = "110011234501";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ── Detalle de caso ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetCasoById_ShouldLeaveALeerCasoExitoRow_WithTheCasesTenant()
    {
        (Guid caso, _) = await SeedCasoAsync(ArlA);

        HttpResponseMessage response = await Client(Analista(ArlA)).GetAsync($"v1/casos/{caso}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FilasAsync(caso)).ShouldBe([("LEER", "CASO", "EXITO", ArlA)]);
    }

    // ── IP real detrás de Nginx (SPEC 13) ─────────────────────────────────────

    [Fact]
    public async Task AuditRow_ShouldRecordTheClientIp_WhenTheRequestComesThroughTheTrustedProxy()
    {
        (Guid caso, _) = await SeedCasoAsync(ArlA);

        string? ip = await IpAuditadaAsync(caso, desde: "10.30.0.5", xForwardedFor: "203.0.113.7");

        ip.ShouldBe("203.0.113.7");
    }

    [Fact]
    public async Task AuditRow_ShouldIgnoreXForwardedFor_FromAnUntrustedOrigin()
    {
        (Guid caso, _) = await SeedCasoAsync(ArlA);

        string? ip = await IpAuditadaAsync(caso, desde: "198.51.100.9", xForwardedFor: "203.0.113.7");

        ip.ShouldBe("198.51.100.9");
    }

    [Fact]
    public async Task GetCasoById_ShouldReturn500WithoutTheData_WhenTheAuditCannotBeWritten()
    {
        (Guid caso, string radicado) = await SeedCasoAsync(ArlA);

        await using var sinAuditoria = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuditWriter>();
                services.AddScoped<IAuditWriter, ThrowingAuditWriter>();
            }));

        HttpClient client = sinAuditoria.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Claims", JsonSerializer.Serialize(Analista(ArlA)));

        HttpResponseMessage response = await client.GetAsync($"v1/casos/{caso}");
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        JsonNode.Parse(body)!["errorCode"]!.GetValue<string>().ShouldBe("Auditoria.NoRegistrada");
        body.ShouldNotContain(radicado);
    }

    [Fact]
    public async Task ListCasos_ShouldNotCreateAuditRows()
    {
        await SeedCasoAsync(ArlA);
        string correlationId = $"list-{Guid.NewGuid():N}";

        using HttpRequestMessage request = new(HttpMethod.Get, "v1/casos?page=1&pageSize=50");
        request.Headers.Add("X-Correlation-Id", correlationId);
        HttpResponseMessage response = await Client(Analista(ArlA)).SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountByCorrelationAsync(correlationId)).ShouldBe(0L);
    }

    // ── Denegados de política ─────────────────────────────────────────────────

    [Fact]
    public async Task RemitirCaso_WithoutCasosWrite_ShouldReturn403_AndLeaveADenegadoRow()
    {
        (Guid caso, _) = await SeedCasoAsync(ArlA);
        var soloLectura = new Dictionary<string, object>
        {
            ["sub"] = TestAuthenticationHandler.TestUserId.ToString(),
            ["iss"] = TestAuthenticationHandler.DefaultIssuer,
            ["actor_tipo"] = "arl",
            ["tenant_id"] = ArlA,
            ["permissions"] = new[] { "casos:read" }
        };

        HttpResponseMessage response = await Client(soloLectura)
            .PostAsJsonAsync($"v1/casos/{caso}/remisiones", new { ipsId = Ips });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await using NpgsqlConnection conn = await OpenSuperuserAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT resultado, accion, recurso_tipo, tenant_id, actor_tenant_id
            FROM auditoria.accesos WHERE caso_medico_id = @caso
            """;
        cmd.Parameters.AddWithValue("caso", caso);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();

        (await reader.ReadAsync()).ShouldBeTrue("no audit row for the denied request");
        reader.GetString(0).ShouldBe("DENEGADO");
        reader.GetString(1).ShouldBe("CREAR");
        reader.GetString(2).ShouldBe("CASO");
        reader.IsDBNull(3).ShouldBeTrue();
        reader.GetString(4).ShouldBe(ArlA);
        (await reader.ReadAsync()).ShouldBeFalse();
    }

    // ── Consulta del oficial de privacidad ────────────────────────────────────

    [Fact]
    public async Task ListAccesos_AsPrivacidadArlA_ShouldReturnOnlyArlARows_IncludingTheIpsAccessesToItsCases()
    {
        (Guid casoA, _) = await SeedCasoAsync(ArlA, remitirA: Ips);
        (Guid casoB, _) = await SeedCasoAsync(ArlB, remitirA: Ips);

        // medico.ips reads both cases; each read is audited with the case's tenant.
        (await Client(MedicoIps()).GetAsync($"v1/casos/{casoA}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client(MedicoIps()).GetAsync($"v1/casos/{casoB}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        HttpClient privacidad = Client(PrivacidadArlA());

        JsonArray todos = await ItemsAsync(privacidad, "pageSize=200");
        todos.Count.ShouldBeGreaterThan(0);
        todos.Select(i => i!["tenantId"]?.GetValue<string>()).ShouldAllBe(t => t == ArlA);
        todos.ShouldAllBe(i => i!["hashRegistro"] == null);

        JsonArray deCasoA = await ItemsAsync(privacidad, $"casoId={casoA}");
        JsonNode lecturaIps = deCasoA.Single(i => i!["actorTipo"]!.GetValue<string>() == "ips")!;
        lecturaIps["actorIpsId"]!.GetValue<string>().ShouldBe(Ips);
        lecturaIps["accion"]!.GetValue<string>().ShouldBe("LEER");
        lecturaIps["recursoTipo"]!.GetValue<string>().ShouldBe("CASO");

        (await ItemsAsync(privacidad, $"casoId={casoB}")).ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAccesos_ShouldAuditItself()
    {
        string correlationId = $"aud-{Guid.NewGuid():N}";

        using HttpRequestMessage request = new(HttpMethod.Get, $"v1/auditoria/accesos?{Rango(TimeSpan.FromHours(1))}");
        request.Headers.Add("X-Correlation-Id", correlationId);
        HttpResponseMessage response = await Client(PrivacidadArlA()).SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountByCorrelationAsync(correlationId, "AND accion = 'LEER' AND recurso_tipo = 'AUDITORIA' AND tenant_id = 'ARL_A'"))
            .ShouldBe(1L);
    }

    [Fact]
    public async Task ListAccesos_WithoutAuditoriaRead_ShouldReturn403()
    {
        HttpResponseMessage response = await Client(Analista(ArlA))
            .GetAsync($"v1/auditoria/accesos?{Rango(TimeSpan.FromHours(1))}");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListAccesos_WithARangeOf32Days_ShouldReturn400()
    {
        HttpResponseMessage response = await Client(PrivacidadArlA())
            .GetAsync($"v1/auditoria/accesos?{Rango(TimeSpan.FromDays(32))}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListAccesos_WithoutDates_ShouldReturn400()
    {
        HttpResponseMessage response = await Client(PrivacidadArlA()).GetAsync("v1/auditoria/accesos");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ── Actors ────────────────────────────────────────────────────────────────

    private static Dictionary<string, object> Analista(string tenantId) => new()
    {
        ["sub"] = TestAuthenticationHandler.TestUserId.ToString(),
        ["iss"] = TestAuthenticationHandler.DefaultIssuer,
        ["actor_tipo"] = "arl",
        ["tenant_id"] = tenantId,
        ["permissions"] = new[] { "casos:read", "casos:write" }
    };

    private static Dictionary<string, object> PrivacidadArlA() => new()
    {
        ["sub"] = "01999000-0000-7000-8000-000000000a09",
        ["iss"] = TestAuthenticationHandler.DefaultIssuer,
        ["actor_tipo"] = "arl",
        ["tenant_id"] = ArlA,
        ["permissions"] = new[] { "auditoria:read", "auditoria:export" }
    };

    private static Dictionary<string, object> MedicoIps() => new()
    {
        ["sub"] = "01999000-0000-7000-8000-000000000c01",
        ["iss"] = TestAuthenticationHandler.DefaultIssuer,
        ["actor_tipo"] = "ips",
        ["ips_id"] = Ips,
        ["permissions"] = new[] { "casos:read", "clinico:read", "clinico:write" }
    };

    private HttpClient Client(Dictionary<string, object> claims) =>
        factory.CreateClientWithHeaders(new Dictionary<string, string>
        {
            ["X-Test-Claims"] = JsonSerializer.Serialize(claims)
        });

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Rango(TimeSpan ancho)
    {
        DateTimeOffset hasta = DateTimeOffset.UtcNow.AddMinutes(5);
        DateTimeOffset desde = hasta - ancho;
        return $"desde={Uri.EscapeDataString(desde.ToString("O", CultureInfo.InvariantCulture))}" +
               $"&hasta={Uri.EscapeDataString(hasta.ToString("O", CultureInfo.InvariantCulture))}";
    }

    private static async Task<JsonArray> ItemsAsync(HttpClient client, string filtros)
    {
        HttpResponseMessage response = await client.GetAsync($"v1/auditoria/accesos?{Rango(TimeSpan.FromHours(1))}&{filtros}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonObject body = (await response.Content.ReadFromJsonAsync<JsonObject>(JsonOptions))!;
        return body["items"]!.AsArray();
    }

    private async Task<List<(string Accion, string Recurso, string Resultado, string? Tenant)>> FilasAsync(Guid caso)
    {
        await using NpgsqlConnection conn = await OpenSuperuserAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT accion, recurso_tipo, resultado, tenant_id FROM auditoria.accesos
            WHERE caso_medico_id = @caso ORDER BY ocurrido_en
            """;
        cmd.Parameters.AddWithValue("caso", caso);

        var filas = new List<(string, string, string, string?)>();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            filas.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return filas;
    }

    /// <summary>
    /// Reads the case as an analyst from <paramref name="desde"/> with <c>X-Forwarded-For</c>, with Nginx's network
    /// (<c>10.30.0.0/24</c>) as the trusted proxy, and returns the IP of the audit row.
    /// </summary>
    private async Task<string?> IpAuditadaAsync(Guid caso, string desde, string xForwardedFor)
    {
        await using var porProxy = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Red:ProxyConfiable", "10.30.0.0/24"));

        HttpClient client = porProxy.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Claims", JsonSerializer.Serialize(Analista(ArlA)));
        client.DefaultRequestHeaders.Add(RemoteIpDePrueba.Cabecera, desde);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", xForwardedFor);

        HttpResponseMessage response = await client.GetAsync($"v1/casos/{caso}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using NpgsqlConnection conn = await OpenSuperuserAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT host(ip) FROM auditoria.accesos WHERE caso_medico_id = @caso";
        cmd.Parameters.AddWithValue("caso", caso);

        return (string?)await cmd.ExecuteScalarAsync();
    }

    private async Task<long> CountByCorrelationAsync(string correlationId, string extra = "")
    {
        await using NpgsqlConnection conn = await OpenSuperuserAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM auditoria.accesos WHERE correlation_id = @corr {extra}";
        cmd.Parameters.AddWithValue("corr", correlationId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<NpgsqlConnection> OpenSuperuserAsync()
    {
        var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>Seeds a case as <c>halsa_owner</c> (RLS is forced, so the owner sets the tenant first).</summary>
    private async Task<(Guid Id, string Radicado)> SeedCasoAsync(string tenantId, string? remitirA = null)
    {
        Guid id = Guid.CreateVersion7();
        string radicado = $"AU-{id:N}";

        await using var conn = new NpgsqlConnection(factory.OwnerConnectionString);
        await conn.OpenAsync();

        await using (NpgsqlCommand set = conn.CreateCommand())
        {
            set.CommandText = "SELECT set_config('app.tenant_id', @tid, false)";
            set.Parameters.AddWithValue("tid", tenantId);
            await set.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand cmd = conn.CreateCommand())
        {
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
            cmd.Parameters.AddWithValue("rad", radicado);
            await cmd.ExecuteNonQueryAsync();
        }

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

        return (id, radicado);
    }

    /// <summary>An audit store that is down: every read must then fail closed.</summary>
    private sealed class ThrowingAuditWriter : IAuditWriter
    {
        public Task RegistrarAsync(
            AccionAuditoria accion,
            ObjetivoAuditoria objetivo,
            TenantId? tenant,
            ResultadoAuditoria resultado,
            CancellationToken cancellationToken,
            string? motivo = null) =>
            throw new InvalidOperationException("Audit store unavailable (test).");
    }
}
