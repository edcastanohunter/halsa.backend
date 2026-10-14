using Halsa.Backend.IntegrationTests.Abstractions;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;

namespace Halsa.Backend.IntegrationTests.Terminos;

/// <summary>
/// Seeding and raw-SQL helpers for the SPEC 09 tests. Cases and plazos are seeded as <c>halsa_owner</c> with the tenant
/// set (RLS is forced). Each test uses its own tenant, so counts never see another test's rows.
/// </summary>
internal static class PlazosSql
{
    public static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);

    /// <summary>A case with explicit event and radication times, for the evaluation of the report plazos.</summary>
    public static async Task<(Guid Id, string Radicado)> InsertCasoAsync(
        DatabaseFixture db,
        string tenantId,
        DateTimeOffset fechaEvento,
        DateTimeOffset radicadoEn,
        string tipoEvento = "AccidenteTrabajo",
        bool esGrave = false,
        string trabajadorNumero = "1020304050",
        string estado = "Radicado")
    {
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenantId);

        Guid id = Guid.CreateVersion7();
        string radicado = $"ATEL-{tenantId}-{id.ToString("N")[^10..]}";

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO atel.casos_medicos
              (id, tenant_id, radicado, tipo_evento, fecha_evento,
               trabajador_tipo, trabajador_numero, empleador_tipo, empleador_numero,
               municipio_divipola, estado, es_grave_o_mortal, radicado_en)
            VALUES
              (@id, @tid, @rad, @tipo, @fecha, 'CC', @trabajador, 'NIT', '900123456', '05001', @estado, @grave, @radicadoEn)
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("rad", radicado);
        cmd.Parameters.AddWithValue("tipo", tipoEvento);
        cmd.Parameters.AddWithValue("fecha", fechaEvento.ToUniversalTime());
        cmd.Parameters.AddWithValue("trabajador", trabajadorNumero);
        cmd.Parameters.AddWithValue("estado", estado);
        cmd.Parameters.AddWithValue("grave", esGrave);
        cmd.Parameters.AddWithValue("radicadoEn", radicadoEn.ToUniversalTime());
        await cmd.ExecuteNonQueryAsync();

        return (id, radicado);
    }

    /// <summary>An open plazo of <paramref name="regla"/>, with explicit times.</summary>
    public static async Task<Guid> InsertPlazoAsync(
        DatabaseFixture db,
        string tenantId,
        Guid casoId,
        string regla,
        DateTimeOffset venceEn,
        DateTimeOffset alertaEn,
        DateTimeOffset? iniciaEn = null,
        Guid? referenciaId = null,
        string estado = "ABIERTO",
        DateOnly? ultimaFechaFin = null,
        string radicado = "ATEL-TEST")
    {
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenantId);

        Guid id = Guid.CreateVersion7();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sst.plazos
              (id, tenant_id, caso_medico_id, regla_id, regla_codigo, referencia_id, radicado,
               inicia_en, vence_en, alerta_en, estado, extemporaneo, ultima_fecha_fin)
            SELECT @id, @tid, @caso, r.id, r.codigo, @ref, @rad, @inicia, @vence, @alerta, @estado, false, @fin
            FROM sst.reglas_termino r
            WHERE r.codigo = @regla
            ORDER BY r.vigente_desde DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("caso", casoId);
        cmd.Parameters.AddWithValue("ref", (object?)referenciaId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rad", radicado);
        cmd.Parameters.AddWithValue("inicia", (iniciaEn ?? venceEn.AddDays(-15)).ToUniversalTime());
        cmd.Parameters.AddWithValue("vence", venceEn.ToUniversalTime());
        cmd.Parameters.AddWithValue("alerta", alertaEn.ToUniversalTime());
        cmd.Parameters.AddWithValue("estado", estado);
        cmd.Parameters.AddWithValue("fin", ultimaFechaFin is { } f ? f : DBNull.Value);
        cmd.Parameters.AddWithValue("regla", regla);
        (await cmd.ExecuteNonQueryAsync()).ShouldBe(1);

        return id;
    }

    /// <summary>A plazo as the <c>postgres</c> superuser sees it (RLS bypassed).</summary>
    public static async Task<PlazoFila> PlazoAsync(DatabaseFixture db, Guid id)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.SuperuserConnectionString);
        return (await LeerAsync(conn, "WHERE id = @p", id)).Single();
    }

    /// <summary>The plazos of a case, by rule code.</summary>
    public static async Task<List<PlazoFila>> PlazosDelCasoAsync(DatabaseFixture db, Guid casoId)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.SuperuserConnectionString);
        return await LeerAsync(conn, "WHERE caso_medico_id = @p ORDER BY regla_codigo, referencia_id", casoId);
    }

    public static async Task EjecutarComoSuperusuarioAsync(DatabaseFixture db, string sql, params (string Name, object Value)[] parametros)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.SuperuserConnectionString);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in parametros)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<PlazoFila>> LeerAsync(NpgsqlConnection conn, string where, Guid parametro)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, tenant_id, caso_medico_id, regla_codigo, referencia_id, radicado, inicia_en, vence_en, alerta_en,
                   estado, alertado_en, cumplido_en, cumplido_por, nota_cumplimiento, extemporaneo, ultima_fecha_fin
            FROM sst.plazos {where}
            """;
        cmd.Parameters.AddWithValue("p", parametro);

        var filas = new List<PlazoFila>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            filas.Add(new PlazoFila(
                r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetGuid(4), r.GetString(5),
                r.GetFieldValue<DateTimeOffset>(6), r.GetFieldValue<DateTimeOffset>(7), r.GetFieldValue<DateTimeOffset>(8),
                r.GetString(9),
                r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10),
                r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11),
                r.IsDBNull(12) ? null : r.GetString(12),
                r.IsDBNull(13) ? null : r.GetString(13),
                r.GetBoolean(14),
                r.IsDBNull(15) ? null : r.GetFieldValue<DateOnly>(15)));
        }

        return filas;
    }
}

internal sealed record PlazoFila(
    Guid Id,
    string TenantId,
    Guid CasoMedicoId,
    string Regla,
    Guid? ReferenciaId,
    string Radicado,
    DateTimeOffset IniciaEn,
    DateTimeOffset VenceEn,
    DateTimeOffset AlertaEn,
    string Estado,
    DateTimeOffset? AlertadoEn,
    DateTimeOffset? CumplidoEn,
    string? CumplidoPor,
    string? Nota,
    bool Extemporaneo,
    DateOnly? UltimaFechaFin);
