using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using Halsa.Backend.FunctionalTests.Clinico;
using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.Infrastructure.Worker;
using Halsa.Backend.Infrastructure.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Clinico.DictamenesSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Plazos;

/// <summary>
/// SPEC 09, paso 9: los endpoints de plazos, la consulta de estado del FURAT con sus plazos, y la controversia del
/// dictamen de punta a punta (notificación → plazo → barrido → <c>EN_FIRME</c>). Cada test usa su propio tenant o caso.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class PlazosEndpointsTests(ApiFactory factory)
{
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);

    // ── POST …/cumplir ────────────────────────────────────────────────────────

    [Fact]
    public async Task Cumplir_WithAFiveCharacterNote_ShouldAnswer400()
    {
        string tenant = TenantNuevo("ARL_PZ_NOTA");
        (Guid caso, Guid plazo) = await PlazoAbiertoAsync(tenant);

        HttpResponseMessage response = await Analista(tenant).PostAsJsonAsync(Cumplir(caso, plazo), new { nota = "corta" }, JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Plazo.NotaRequerida");
        (await EstadoAsync(plazo)).ShouldBe("ABIERTO");
    }

    [Fact]
    public async Task Cumplir_WithAValidNote_ShouldAnswer204_LeaveItCumplido_AndAnActualizarPlazoRow()
    {
        string tenant = TenantNuevo("ARL_PZ_CUMPLIR");
        (Guid caso, Guid plazo) = await PlazoAbiertoAsync(tenant);

        HttpResponseMessage response = await Analista(tenant).PostAsJsonAsync(
            Cumplir(caso, plazo), new { nota = "Investigación radicada en el SG-SST" }, JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await EstadoAsync(plazo)).ShouldBe("CUMPLIDO");
        (await ContarAsync(factory, """
            SELECT count(*) FROM sst.plazos
            WHERE id = @id AND nota_cumplimiento = 'Investigación radicada en el SG-SST' AND NOT extemporaneo
              AND cumplido_por = @sub AND cumplido_en IS NOT NULL
            """, ("id", plazo), ("sub", AnalistaSub))).ShouldBe(1);
        (await AuditoriaAsync(factory, caso)).ShouldContain(
            new AuditoriaFila("ACTUALIZAR", "PLAZO", plazo.ToString(), "EXITO", tenant, "halsa-backoffice"));
    }

    [Fact]
    public async Task Cumplir_AVencidoPlazo_ShouldLeaveItExtemporaneo_AndASecondTimeAnswer409()
    {
        string tenant = TenantNuevo("ARL_PZ_TARDE");
        (Guid caso, Guid plazo) = await PlazoAbiertoAsync(tenant, estado: "VENCIDO");
        HttpClient analista = Analista(tenant);

        (await analista.PostAsJsonAsync(Cumplir(caso, plazo), new { nota = "Cumplido después del vencimiento" }, JsonOptions))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ContarAsync(factory, "SELECT count(*) FROM sst.plazos WHERE id = @id AND extemporaneo", ("id", plazo)))
            .ShouldBe(1);

        HttpResponseMessage segunda = await analista.PostAsJsonAsync(
            Cumplir(caso, plazo), new { nota = "Cumplido otra vez, por error" }, JsonOptions);
        segunda.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(segunda)).ShouldBe("Plazo.NoCumplible");
    }

    [Fact]
    public async Task Cumplir_APlazoOfAnotherTenant_ShouldAnswer404()
    {
        (Guid caso, Guid plazo) = await PlazoAbiertoAsync(TenantNuevo("ARL_PZ_AJENO"));

        HttpResponseMessage response = await Analista(TenantNuevo("ARL_PZ_OTRO")).PostAsJsonAsync(
            Cumplir(caso, plazo), new { nota = "Intento sobre otro tenant" }, JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await EstadoAsync(plazo)).ShouldBe("ABIERTO");
    }

    [Fact]
    public async Task Cumplir_WithoutPlazosWrite_ShouldAnswer403()
    {
        string tenant = TenantNuevo("ARL_PZ_SIN_PERMISO");
        (Guid caso, Guid plazo) = await PlazoAbiertoAsync(tenant);

        HttpResponseMessage response = await Analista(tenant, escribe: false).PostAsJsonAsync(
            Cumplir(caso, plazo), new { nota = "Sin el permiso de escritura" }, JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ── GET ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListPlazosCaso_ShouldReturnTheRuleItsLegalBasisAndItsState()
    {
        string tenant = TenantNuevo("ARL_PZ_CASO");
        (Guid caso, Guid plazo) = await PlazoAbiertoAsync(tenant);

        HttpResponseMessage response = await Analista(tenant).GetAsync($"v1/casos/{caso}/plazos");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonObject item = (await response.Content.ReadFromJsonAsync<JsonArray>(JsonOptions))!.Single()!.AsObject();
        item["id"]!.GetValue<Guid>().ShouldBe(plazo);
        item["regla"]!.GetValue<string>().ShouldBe("INVESTIGACION_AT");
        item["baseLegal"]!.GetValue<string>().ShouldBe("Res. 1401/2007; Decreto 1072/2015, art. 2.2.4.6.32");
        item["estado"]!.GetValue<string>().ShouldBe("ABIERTO");
        item["extemporaneo"]!.GetValue<bool>().ShouldBeFalse();
        item["cumplidoEn"].ShouldBeNull();
        (await AuditoriaAsync(factory, caso)).ShouldContain(f => f.Accion == "LEER" && f.Recurso == "PLAZO");
    }

    [Fact]
    public async Task ListPlazosCaso_OfAnotherTenant_ShouldAnswer404()
    {
        (Guid caso, _) = await PlazoAbiertoAsync(TenantNuevo("ARL_PZ_CASO_AJENO"));

        (await Analista(TenantNuevo("ARL_PZ_CASO_OTRO")).GetAsync($"v1/casos/{caso}/plazos"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListPlazos_ShouldStayInTheTenant_AndFilterByEstado()
    {
        string tenant = TenantNuevo("ARL_PZ_LISTA");
        (_, Guid abierto) = await PlazoAbiertoAsync(tenant);
        (_, Guid vencido) = await PlazoAbiertoAsync(tenant, estado: "VENCIDO");
        await PlazoAbiertoAsync(TenantNuevo("ARL_PZ_LISTA_OTRO"));

        JsonObject todos = await PaginaAsync(tenant, "v1/plazos");
        todos["totalCount"]!.GetValue<int>().ShouldBe(2);

        JsonObject vencidos = await PaginaAsync(tenant, "v1/plazos?estado=VENCIDO");
        vencidos["items"]!.AsArray().Select(i => i!["id"]!.GetValue<Guid>()).ShouldBe([vencido]);

        JsonObject antes = await PaginaAsync(tenant,
            $"v1/plazos?venceAntesDe={Uri.EscapeDataString(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture))}");
        antes["items"]!.AsArray().Select(i => i!["id"]!.GetValue<Guid>()).ShouldBe([abierto, vencido], ignoreOrder: true);
    }

    [Fact]
    public async Task ListPlazos_WithAnUnknownEstado_ShouldAnswer400() =>
        (await Analista(TenantNuevo("ARL_PZ_ESTADO")).GetAsync("v1/plazos?estado=PERDIDO"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

    [Fact]
    public async Task AnIpsActor_ShouldGet403() =>
        (await Client(factory, MedicoIps()).GetAsync("v1/plazos")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

    // ── La consulta de estado del FURAT ───────────────────────────────────────

    [Fact]
    public async Task FuratStatus_ShouldIncludeTheOpenPlazosOfItsCase()
    {
        string tenant = TenantNuevo("ARL_PZ_FURAT");
        HttpClient integracion = IntegracionArl(factory, tenant);
        JsonObject radicacion = await BodyAsync(await PostAsync(
            integracion, "v1/integraciones/furat", Furat($"POS-{Guid.NewGuid():N}"[..30])));
        radicacion["plazos"]!.AsArray().ShouldBeEmpty();

        await ProcesarOutboxAsync();

        JsonObject estado = await BodyAsync(await integracion.GetAsync(
            $"v1/integraciones/furat/{radicacion["solicitudId"]!.GetValue<Guid>()}"));
        JsonArray plazos = estado["plazos"]!.AsArray();

        // The example is a grave AT of 7 Oct 2026, radicated later than two working days: REPORTE_FURAT is not open.
        plazos.Select(p => p!["regla"]!.GetValue<string>())
            .ShouldBe(["INVESTIGACION_AT", "REMISION_INVESTIGACION_GRAVE"], ignoreOrder: true);
        plazos.ShouldAllBe(p => p!["baseLegal"]!.GetValue<string>().StartsWith("Res. 1401/2007", StringComparison.Ordinal));
    }

    // ── La controversia del dictamen ──────────────────────────────────────────

    [Fact]
    public async Task NotifiedDictamen_ShouldShowVenceControversiaEn_AndBecomeEnFirmeWhenItExpires()
    {
        Guid caso = await CasoAsignadoAsync(factory);
        await RegistradoAsync(factory, caso);
        (await Calificador(factory).PostAsJsonAsync(RutaNotificar(caso), new
            {
                notificadoEn = DateTimeOffset.UtcNow.AddHours(-1).ToString("O", CultureInfo.InvariantCulture),
                medio = "PERSONAL"
            }, JsonOptions))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await ProcesarOutboxAsync();

        DateTimeOffset venceEn = await VenceControversiaAsync(caso);
        JsonObject antes = await BodyAsync(await Calificador(factory).GetAsync(RutaDictamen(caso)));
        antes["estadoNotificacion"]!.GetValue<string>().ShouldBe("NOTIFICADO");
        antes["venceControversiaEn"]!.GetValue<string>()
            .ShouldBe(venceEn.ToOffset(Bogota).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        await new BarridoPlazosJob(
                factory.Services.GetRequiredService<BaseDatosWorker>(),
                new RelojFijo(venceEn.AddMinutes(1)),
                NullLogger<BarridoPlazosJob>.Instance)
            .BarrerAsync(CancellationToken.None);
        await ProcesarOutboxAsync();

        JsonObject despues = await BodyAsync(await Calificador(factory).GetAsync(RutaDictamen(caso)));
        despues["estadoNotificacion"]!.GetValue<string>().ShouldBe("EN_FIRME");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private const string AnalistaSub = "01999000-0000-7000-8000-000000000a01";

    private static string Cumplir(Guid caso, Guid plazo) => $"v1/casos/{caso}/plazos/{plazo}/cumplir";

    /// <summary><c>analista.arla</c> of <paramref name="tenant"/>, with <c>plazos:write</c> as the realm now gives it.</summary>
    private HttpClient Analista(string tenant, bool escribe = true) => Client(factory, new Dictionary<string, object>
    {
        ["sub"] = AnalistaSub,
        ["iss"] = TestAuthenticationHandler.DefaultIssuer,
        ["azp"] = "halsa-backoffice",
        ["actor_tipo"] = "arl",
        ["tenant_id"] = tenant,
        ["permissions"] = escribe
            ? new[] { "casos:read", "casos:write", "incapacidades:read", "plazos:read", "plazos:write" }
            : new[] { "casos:read", "casos:write", "incapacidades:read", "plazos:read" }
    });

    private async Task<JsonObject> PaginaAsync(string tenant, string ruta)
    {
        HttpResponseMessage response = await Analista(tenant).GetAsync(ruta);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await BodyAsync(response);
    }

    /// <summary>A case of <paramref name="tenant"/> and an <c>INVESTIGACION_AT</c> plazo, seeded as <c>halsa_owner</c>.</summary>
    private async Task<(Guid Caso, Guid Plazo)> PlazoAbiertoAsync(string tenant, string estado = "ABIERTO")
    {
        Guid caso = await SeedCasoAsync(factory, tenant);
        Guid plazo = Guid.CreateVersion7();

        await using var conn = new NpgsqlConnection(factory.OwnerConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT set_config('app.tenant_id', @tid, false);
            INSERT INTO sst.plazos
              (id, tenant_id, caso_medico_id, regla_id, regla_codigo, referencia_id, radicado, inicia_en, vence_en,
               alerta_en, estado, extemporaneo)
            SELECT @id, @tid, @caso, r.id, r.codigo, gen_random_uuid(), 'ATEL-TEST', TIMESTAMPTZ '2030-06-01 10:00-05',
                   TIMESTAMPTZ '2030-06-16 23:59:59-05', TIMESTAMPTZ '2030-06-13 23:59:59-05', @estado, false
            FROM sst.reglas_termino r WHERE r.codigo = 'INVESTIGACION_AT';
            """;
        cmd.Parameters.AddWithValue("tid", tenant);
        cmd.Parameters.AddWithValue("id", plazo);
        cmd.Parameters.AddWithValue("caso", caso);
        cmd.Parameters.AddWithValue("estado", estado);
        await cmd.ExecuteNonQueryAsync();

        return (caso, plazo);
    }

    private async Task<string> EstadoAsync(Guid plazo)
    {
        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT estado FROM sst.plazos WHERE id = @id";
        cmd.Parameters.AddWithValue("id", plazo);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<DateTimeOffset> VenceControversiaAsync(Guid caso)
    {
        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT vence_en FROM sst.plazos WHERE caso_medico_id = @caso AND regla_codigo = 'CONTROVERSIA_DICTAMEN'
            """;
        cmd.Parameters.AddWithValue("caso", caso);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue("the notification did not open CONTROVERSIA_DICTAMEN");
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    /// <summary>The hosted outbox dispatcher is removed in tests; drain it the way it would run.</summary>
    private async Task ProcesarOutboxAsync()
    {
        var processor = new OutboxProcessor(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OutboxOptions { BatchSize = 100, MaxAttempts = 5 }),
            NullLogger<OutboxProcessor>.Instance);

        while (await processor.ProcessBatchAsync(CancellationToken.None) > 0)
        {
        }
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora.ToUniversalTime();
    }
}
