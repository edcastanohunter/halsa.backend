using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using Halsa.Backend.FunctionalTests.Integraciones;
using Npgsql;

namespace Halsa.Backend.FunctionalTests.Clinico;

/// <summary>
/// Seeding, actors and raw-SQL helpers for the SPEC 06 tests. Cases and remissions are seeded as
/// <c>halsa_owner</c> (RLS is forced, so the owner sets the tenant first); assertions read as the
/// <c>postgres</c> superuser, which bypasses RLS.
/// </summary>
internal static class ConceptosSeed
{
    public const string ArlA = "ARL_A";
    public const string ArlB = "ARL_B";
    public const string Ips = "110011234501";

    // ── Actors (as the realm seeds them) ──────────────────────────────────────

    public const string MedicoIpsSub = "01999000-0000-7000-8000-000000000c01";
    public const string CalificadorArlASub = "01999000-0000-7000-8000-000000000a02";

    /// <summary><c>medico.ips</c>, with the professional claims of the realm seed.</summary>
    public static Dictionary<string, object> MedicoIps() => new()
    {
        ["sub"] = MedicoIpsSub,
        ["iss"] = TestAuthenticationHandler.DefaultIssuer,
        ["azp"] = "halsa-backoffice",
        ["actor_tipo"] = "ips",
        ["ips_id"] = Ips,
        ["tipo_documento"] = "CC",
        ["numero_documento"] = "80000222",
        ["rethus"] = "RM-DEV-0002",
        ["permissions"] = new[] { "casos:read", "clinico:read", "clinico:write", "incapacidades:read", "incapacidades:write" }
    };

    /// <summary><c>calificador.arla</c>, with the professional claims of the realm seed.</summary>
    public static Dictionary<string, object> CalificadorArlA() => new()
    {
        ["sub"] = CalificadorArlASub,
        ["iss"] = TestAuthenticationHandler.DefaultIssuer,
        ["azp"] = "halsa-backoffice",
        ["actor_tipo"] = "arl",
        ["tenant_id"] = ArlA,
        ["tipo_documento"] = "CC",
        ["numero_documento"] = "52000111",
        ["rethus"] = "RM-DEV-0001",
        ["permissions"] = new[]
        {
            "casos:read", "casos:write", "incapacidades:read", "plazos:read", "clinico:read", "clinico:write",
            "incapacidades:write", "pcl:dictamen:read", "pcl:dictamen:write"
        }
    };

    /// <summary><c>analista.arla</c>: no clinical permission at all.</summary>
    public static Dictionary<string, object> AnalistaArlA() => new()
    {
        ["sub"] = "01999000-0000-7000-8000-000000000a01",
        ["iss"] = TestAuthenticationHandler.DefaultIssuer,
        ["azp"] = "halsa-backoffice",
        ["actor_tipo"] = "arl",
        ["tenant_id"] = ArlA,
        ["permissions"] = new[] { "casos:read", "casos:write", "incapacidades:read", "plazos:read" }
    };

    public static HttpClient Client(ApiFactory factory, Dictionary<string, object> claims) =>
        factory.CreateClientWithHeaders(new Dictionary<string, string>
        {
            ["X-Test-Claims"] = JsonSerializer.Serialize(claims)
        });

    // ── Bodies ────────────────────────────────────────────────────────────────

    /// <summary>The backoffice body of the spec: a <c>CONCEPTO_MEDICO</c> with one CIE-10 diagnosis.</summary>
    public static JsonObject Registro(
        string tipo = "CONCEPTO_MEDICO",
        DateTimeOffset? atendidoEn = null,
        Guid? corrigeA = null,
        string codigo = "S62.6") => new()
    {
        ["tipo"] = tipo,
        ["atendidoEn"] = (atendidoEn ?? DateTimeOffset.UtcNow.AddMinutes(-10)).ToString("O", CultureInfo.InvariantCulture),
        ["corrigeA"] = corrigeA?.ToString(),
        ["contenido"] = new JsonObject
        {
            ["motivo"] = "Control postquirúrgico",
            ["anamnesis"] = "Refiere dolor leve.",
            ["examenFisico"] = "Herida limpia.",
            ["analisis"] = "Evolución favorable.",
            ["plan"] = "Terapia física.",
            ["diagnosticos"] = new JsonArray(new JsonObject
            {
                ["sistema"] = "CIE-10",
                ["codigo"] = codigo,
                ["descripcion"] = "Fractura de otro dedo de la mano"
            }),
            ["observaciones"] = null
        }
    };

    /// <summary>Bodies are read with <see cref="IngestaClientes.BodyAsync"/>.</summary>
    public static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await IngestaClientes.BodyAsync(response))["errorCode"]?.GetValue<string>();

    // ── atel seed (as halsa_owner) ────────────────────────────────────────────

    /// <param name="fechaEvento">Default: a day ago.</param>
    public static async Task<Guid> SeedCasoAsync(
        ApiFactory factory, string tenantId, string? remitirA = null, DateTimeOffset? fechaEvento = null,
        string estado = "EnAtencion")
    {
        Guid id = Guid.CreateVersion7();

        await using NpgsqlConnection conn = await OpenOwnerAsync(factory, tenantId);
        await using (NpgsqlCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO atel.casos_medicos
                  (id, tenant_id, radicado, tipo_evento, fecha_evento,
                   trabajador_tipo, trabajador_numero, empleador_tipo, empleador_numero,
                   municipio_divipola, estado, es_grave_o_mortal, radicado_en)
                VALUES
                  (@id, @tid, @rad, 'AccidenteTrabajo', @fecha, 'CC', '123456', 'NIT', '9001',
                   '11001', @estado, false, now());
                """;
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("tid", tenantId);
            cmd.Parameters.AddWithValue("rad", $"CM-{id:N}");
            cmd.Parameters.AddWithValue("fecha", (fechaEvento ?? DateTimeOffset.UtcNow.AddDays(-1)).ToUniversalTime());
            cmd.Parameters.AddWithValue("estado", estado);
            await cmd.ExecuteNonQueryAsync();
        }

        if (remitirA is not null)
        {
            await RemitirAsync(factory, id, tenantId, remitirA);
        }

        return id;
    }

    public static async Task<Guid> RemitirAsync(ApiFactory factory, Guid casoId, string tenantId, string ipsId)
    {
        Guid id = Guid.CreateVersion7();

        await using NpgsqlConnection conn = await OpenOwnerAsync(factory, tenantId);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO atel.remisiones (id, caso_medico_id, tenant_id, ips_id, remitido_en, remitido_por)
            VALUES (@rid, @id, @tid, @ips, now(), 'sub-seed');
            """;
        cmd.Parameters.AddWithValue("rid", id);
        cmd.Parameters.AddWithValue("id", casoId);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("ips", ipsId);
        await cmd.ExecuteNonQueryAsync();

        return id;
    }

    public static async Task RevocarAsync(ApiFactory factory, Guid casoId, string tenantId)
    {
        await using NpgsqlConnection conn = await OpenOwnerAsync(factory, tenantId);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE atel.remisiones SET revocada_en = now(), revocada_por = 'sub-seed'
            WHERE caso_medico_id = @id AND revocada_en IS NULL
            """;
        cmd.Parameters.AddWithValue("id", casoId);
        (await cmd.ExecuteNonQueryAsync()).ShouldBeGreaterThan(0);
    }

    // ── Assertions (as the superuser) ─────────────────────────────────────────

    /// <summary>The clinical records of the case, oldest first, with the columns the spec checks.</summary>
    public static async Task<List<RegistroFila>> RegistrosAsync(ApiFactory factory, Guid casoId)
    {
        await using NpgsqlConnection conn = await OpenSuperuserAsync(factory);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, tenant_id, tipo, profesional_sub, profesional_registro_rethus, contenido_key_id, corrige_a
            FROM clinico.historia_clinica_detalle WHERE caso_medico_id = @caso ORDER BY registrado_en, id
            """;
        cmd.Parameters.AddWithValue("caso", casoId);

        var filas = new List<RegistroFila>();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            filas.Add(new RegistroFila(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6)));
        }

        return filas;
    }

    /// <summary>The audit rows of the case, oldest first.</summary>
    public static async Task<List<AuditoriaFila>> AuditoriaAsync(ApiFactory factory, Guid casoId)
    {
        await using NpgsqlConnection conn = await OpenSuperuserAsync(factory);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT accion, recurso_tipo, recurso_id, resultado, tenant_id, client_id FROM auditoria.accesos
            WHERE caso_medico_id = @caso ORDER BY ocurrido_en
            """;
        cmd.Parameters.AddWithValue("caso", casoId);

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

    /// <summary>The active <c>CONTENIDO</c> DEK of the tenant.</summary>
    public static async Task<string> LlaveContenidoAsync(ApiFactory factory, string tenantId)
    {
        await using NpgsqlConnection conn = await OpenSuperuserAsync(factory);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT key_id FROM clinico.llaves_tenant
            WHERE tenant_id = @tid AND proposito = 'CONTENIDO' AND activa
            """;
        cmd.Parameters.AddWithValue("tid", tenantId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task<long> ContarAsync(ApiFactory factory, string sql, params (string Name, object Value)[] parametros)
    {
        await using NpgsqlConnection conn = await OpenSuperuserAsync(factory);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in parametros)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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

    private static async Task<NpgsqlConnection> OpenSuperuserAsync(ApiFactory factory)
    {
        var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}

internal sealed record RegistroFila(
    Guid Id, string TenantId, string Tipo, string ProfesionalSub, string? Rethus, string ContenidoKeyId, Guid? CorrigeA);

internal sealed record AuditoriaFila(
    string Accion, string Recurso, string? RecursoId, string Resultado, string? TenantId, string? ClientId);
