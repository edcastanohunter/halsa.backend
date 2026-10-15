using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using Halsa.Backend.FunctionalTests.Clinico;
using Npgsql;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Pacientes;

/// <summary>
/// The actors and the seed of the SPEC 12 tests. Every test uses its own worker document (the other suites all use
/// <c>CC 123456</c>) and its own patient <c>sub</c>, so a link never reaches another test's cases.
/// </summary>
internal static class PacientesSeed
{
    public const string AnalistaSub = "01999000-0000-7000-8000-000000000a01";
    public const string PrivacidadSub = "01999000-0000-7000-8000-000000000a03";

    public static string NuevoSub() => Guid.CreateVersion7().ToString();

    public static string NuevoDocumento() =>
        $"7{Random.Shared.NextInt64(100_000_000_000, 999_999_999_999).ToString(CultureInfo.InvariantCulture)}";

    // ── Actors ────────────────────────────────────────────────────────────────

    /// <summary>A patient of <c>halsa-pacientes</c>, as the BFF forwards it: only <c>sub</c> and the issuer.</summary>
    public static HttpClient Paciente(ApiFactory factory, string sub) => Client(factory, new Dictionary<string, object>
    {
        ["sub"] = sub,
        ["iss"] = TestAuthenticationHandler.PacientesIssuer,
        ["azp"] = "halsa-bff-paciente",
        ["email"] = "paciente.dev@halsa.dev"
    });

    /// <summary><c>analista.arla</c> of <paramref name="tenant"/>, with <c>pacientes:activar</c> unless told otherwise.</summary>
    public static HttpClient Analista(ApiFactory factory, string tenant, bool activa = true) =>
        Client(factory, new Dictionary<string, object>
        {
            ["sub"] = AnalistaSub,
            ["iss"] = TestAuthenticationHandler.DefaultIssuer,
            ["azp"] = "halsa-backoffice",
            ["actor_tipo"] = "arl",
            ["tenant_id"] = tenant,
            ["permissions"] = activa
                ? new[] { "casos:read", "casos:write", "casos:asignar", "incapacidades:read", "plazos:read", "plazos:write", "pacientes:activar" }
                : new[] { "casos:read", "casos:write" }
        });

    /// <summary><c>privacidad.arla</c> of <paramref name="tenant"/>: the audit and the HC copy inbox.</summary>
    public static HttpClient Privacidad(ApiFactory factory, string tenant) => Client(factory, new Dictionary<string, object>
    {
        ["sub"] = PrivacidadSub,
        ["iss"] = TestAuthenticationHandler.DefaultIssuer,
        ["azp"] = "halsa-backoffice",
        ["actor_tipo"] = "arl",
        ["tenant_id"] = tenant,
        ["permissions"] = new[] { "auditoria:read", "auditoria:export", "auditoria:revisar", "solicitudes-hc:gestionar" }
    });

    // ── Flows ─────────────────────────────────────────────────────────────────

    public static string RutaCodigos(Guid caso) => $"v1/casos/{caso}/codigos-activacion";

    /// <summary>Issues a code through the API and returns it whole.</summary>
    public static async Task<string> EmitirAsync(ApiFactory factory, string tenant, Guid caso)
    {
        HttpResponseMessage response = await Analista(factory, tenant).PostAsync(RutaCodigos(caso), content: null);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        return (await BodyAsync(response))["codigo"]!.GetValue<string>();
    }

    public static Task<HttpResponseMessage> CanjearAsync(HttpClient paciente, string codigo) =>
        paciente.PostAsJsonAsync("v1/paciente/vinculos", new { codigo }, JsonOptions);

    /// <summary>A case of <paramref name="documento"/> issued, redeemed by a new patient: returns the patient's client.</summary>
    public static async Task<HttpClient> VinculadoAsync(ApiFactory factory, string tenant, Guid caso)
    {
        HttpClient paciente = Paciente(factory, NuevoSub());
        (await CanjearAsync(paciente, await EmitirAsync(factory, tenant, caso))).StatusCode.ShouldBe(HttpStatusCode.Created);
        return paciente;
    }

    /// <summary>The same 6 + 10 characters with one character of the secret changed.</summary>
    public static string ConOtroSecreto(string codigo) =>
        string.Concat(codigo.AsSpan(0, codigo.Length - 1), codigo[^1] == '0' ? "1" : "0");

    // ── atel seed (as halsa_owner) ────────────────────────────────────────────

    /// <summary>A case of <paramref name="documento"/>, <c>Validado</c> unless told otherwise.</summary>
    public static async Task<Guid> CasoDelTrabajadorAsync(
        ApiFactory factory, string tenant, string documento, string estado = "Validado", string tipoEvento = "AccidenteTrabajo")
    {
        Guid id = Guid.CreateVersion7();

        await using NpgsqlConnection conn = await OpenOwnerAsync(factory, tenant);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO atel.casos_medicos
              (id, tenant_id, radicado, tipo_evento, fecha_evento,
               trabajador_tipo, trabajador_numero, empleador_tipo, empleador_numero,
               municipio_divipola, estado, es_grave_o_mortal, radicado_en, validado_en)
            VALUES
              (@id, @tid, @rad, @tipo, now() - interval '3 days', 'CC', @doc, 'NIT', '9001',
               '11001', @estado, false, now() - interval '2 days',
               CASE WHEN @estado IN ('Radicado', 'Rechazado') THEN NULL ELSE now() - interval '1 day' END);
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", tenant);
        cmd.Parameters.AddWithValue("rad", $"PAC-{id:N}");
        cmd.Parameters.AddWithValue("tipo", tipoEvento);
        cmd.Parameters.AddWithValue("doc", documento);
        cmd.Parameters.AddWithValue("estado", estado);
        await cmd.ExecuteNonQueryAsync();

        return id;
    }

    /// <summary>A row of <c>atel.codigos_activacion</c>, read as the superuser.</summary>
    public static async Task<JsonObject> CodigoAsync(ApiFactory factory, string id)
    {
        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT to_jsonb(c) - 'secreto_hash' - 'secreto_sal'
                   || jsonb_build_object('hash_len', octet_length(secreto_hash), 'sal_len', octet_length(secreto_sal))
            FROM atel.codigos_activacion c WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("id", id);
        return JsonNode.Parse((string)(await cmd.ExecuteScalarAsync())!)!.AsObject();
    }

    /// <summary>Every text column of the codes table, concatenated: to prove the secret is nowhere.</summary>
    public static async Task<string> TodoElCodigoAsync(ApiFactory factory, string id)
    {
        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT to_jsonb(c)::text FROM atel.codigos_activacion c WHERE id = @id";
        cmd.Parameters.AddWithValue("id", id);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>The audit rows of a resource type by an actor, oldest first: those without a case too.</summary>
    public static async Task<List<AuditoriaFila>> AuditoriaDeActorAsync(ApiFactory factory, string actorId, string recurso)
    {
        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT accion, recurso_tipo, recurso_id, resultado, tenant_id, client_id FROM auditoria.accesos
            WHERE actor_id = @actor AND recurso_tipo = @recurso ORDER BY ocurrido_en
            """;
        cmd.Parameters.AddWithValue("actor", actorId);
        cmd.Parameters.AddWithValue("recurso", recurso);

        var filas = new List<AuditoriaFila>();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            filas.Add(new AuditoriaFila(
                reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return filas;
    }

    private static async Task<NpgsqlConnection> OpenOwnerAsync(ApiFactory factory, string tenantId)
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
