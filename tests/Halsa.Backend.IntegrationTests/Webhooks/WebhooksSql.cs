using Halsa.Backend.IntegrationTests.Abstractions;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;

namespace Halsa.Backend.IntegrationTests.Webhooks;

/// <summary>
/// Seeding and raw-SQL helpers for the SPEC 10 tests. Subscriptions are seeded and read as the <c>postgres</c> superuser,
/// which bypasses RLS. Each test uses its own tenant or IPS, so it never sees another test's subscriptions.
/// </summary>
internal static class WebhooksSql
{
    /// <summary>A subscription of <paramref name="emisor"/> (<c>tenant:X</c> or <c>ips:Y</c>), with a dummy wrapped secret.</summary>
    public static async Task<Guid> InsertSuscripcionAsync(
        DatabaseFixture db,
        string emisor,
        string[] eventos,
        string estado = "ACTIVA",
        string url = "https://receptor.example.com/halsa",
        byte[]? secretoCt = null,
        string secretoKeyId = "local:v1",
        Guid? id = null)
    {
        Guid suscripcionId = id ?? Guid.CreateVersion7();
        bool esTenant = emisor.StartsWith("tenant:", StringComparison.Ordinal);
        string destino = emisor[(emisor.IndexOf(':', StringComparison.Ordinal) + 1)..];

        await using NpgsqlConnection conn = await OpenAsync(db.SuperuserConnectionString);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO integracion.suscripciones_webhook
              (id, emisor, tenant_id, ips_id, url, eventos, estado, secreto_ct, secreto_key_id, fallos_consecutivos,
               creada_en, creada_por)
            VALUES (@id, @emisor, @tenant, @ips, @url, @eventos, @estado, @ct, @kid, 0, now(), 'sub-test')
            """;
        cmd.Parameters.AddWithValue("id", suscripcionId);
        cmd.Parameters.AddWithValue("emisor", emisor);
        cmd.Parameters.AddWithValue("tenant", esTenant ? destino : DBNull.Value);
        cmd.Parameters.AddWithValue("ips", esTenant ? DBNull.Value : destino);
        cmd.Parameters.AddWithValue("url", url);
        cmd.Parameters.AddWithValue("eventos", eventos);
        cmd.Parameters.AddWithValue("estado", estado);
        cmd.Parameters.AddWithValue("ct", secretoCt ?? [0x01]);
        cmd.Parameters.AddWithValue("kid", secretoKeyId);
        await cmd.ExecuteNonQueryAsync();

        return suscripcionId;
    }

    public sealed record EntregaFila(
        Guid Id,
        Guid SuscripcionId,
        Guid EventoId,
        string Tipo,
        string Payload,
        string Estado,
        int Intentos,
        DateTimeOffset PrimerIntentoEn,
        DateTimeOffset ProximoIntentoEn,
        int? UltimoStatus,
        string? UltimoError,
        DateTimeOffset? EntregadaEn);

    public static async Task<List<EntregaFila>> EntregasAsync(DatabaseFixture db, Guid suscripcion)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.SuperuserConnectionString);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, suscripcion_id, evento_id, tipo, payload::text, estado, intentos, primer_intento_en,
                   proximo_intento_en, ultimo_status, ultimo_error, entregada_en
            FROM integracion.entregas_webhook WHERE suscripcion_id = @s ORDER BY primer_intento_en, id
            """;
        cmd.Parameters.AddWithValue("s", suscripcion);

        var filas = new List<EntregaFila>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            filas.Add(new EntregaFila(
                r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6),
                r.GetFieldValue<DateTimeOffset>(7), r.GetFieldValue<DateTimeOffset>(8),
                r.IsDBNull(9) ? null : r.GetInt32(9), r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11)));
        }

        return filas;
    }

    public static async Task<(string Estado, int Fallos)> SuscripcionAsync(DatabaseFixture db, Guid id)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.SuperuserConnectionString);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT estado, fallos_consecutivos FROM integracion.suscripciones_webhook WHERE id = @id";
        cmd.Parameters.AddWithValue("id", id);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.GetString(0), r.GetInt32(1));
    }

    public static async Task EjecutarAsync(DatabaseFixture db, string sql, params (string Name, object Value)[] parametros)
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

    /// <summary>
    /// Takes the subscriptions out of every later dispatcher run (it only sends to active ones), so a test never leaves a
    /// pending delivery for another test's dispatcher.
    /// </summary>
    public static Task BajaAsync(DatabaseFixture db, params Guid[] suscripciones) => EjecutarAsync(db,
        "UPDATE integracion.suscripciones_webhook SET estado = 'ELIMINADA' WHERE id = ANY(@s)", ("s", suscripciones));

    /// <summary>A tenant code no other test uses (20 characters at most).</summary>
    public static string TenantNuevo(string prefijo) => $"{prefijo}_{Guid.NewGuid():N}"[..20].ToUpperInvariant();

    /// <summary>A REPS code no other test uses.</summary>
    public static string IpsNueva() => "8" + Random.Shared.NextInt64(10_000_000_000, 99_999_999_999).ToString(
        System.Globalization.CultureInfo.InvariantCulture);
}
