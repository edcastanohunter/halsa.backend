using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using Npgsql;

namespace Halsa.Backend.FunctionalTests.Integraciones.Webhooks;

/// <summary>The SPEC 10 actors and the raw-SQL helpers of the subscription tests. Assertions read as the superuser.</summary>
internal static class WebhooksClientes
{
    public const string Ruta = "v1/integraciones/suscripciones-webhook";

    /// <summary><c>int-arl-{tenant}</c>, with <c>webhooks:manage</c>.</summary>
    public static HttpClient IntegracionArl(ApiFactory factory, string tenant) => ConClaims(factory, new Dictionary<string, object>
    {
        ["sub"] = $"int-arl-{tenant}",
        ["iss"] = TestAuthenticationHandler.DefaultIssuer,
        ["azp"] = $"int-arl-{tenant}",
        ["actor_tipo"] = "integracion",
        ["tenant_id"] = tenant,
        ["permissions"] = new[] { "integracion:furat:write", "integracion:furep:write", "webhooks:manage" }
    });

    /// <summary><c>int-ips-{reps}</c>, with <c>webhooks:manage</c> (SPEC 10 adds it to <c>integracion-ips</c>).</summary>
    public static HttpClient IntegracionIps(ApiFactory factory, string reps) => ConClaims(factory, new Dictionary<string, object>
    {
        ["sub"] = $"int-ips-{reps}",
        ["iss"] = TestAuthenticationHandler.DefaultIssuer,
        ["azp"] = $"int-ips-{reps}",
        ["actor_tipo"] = "integracion",
        ["ips_id"] = reps,
        ["permissions"] = new[] { "integracion:clinico:write", "incapacidades:write", "webhooks:manage" }
    });

    /// <summary>An ARL person who somehow holds <c>webhooks:manage</c>: still not an integration actor.</summary>
    public static HttpClient AnalistaConPermiso(ApiFactory factory, string tenant) => ConClaims(factory, new Dictionary<string, object>
    {
        ["sub"] = "01999000-0000-7000-8000-00000000a0aa",
        ["iss"] = TestAuthenticationHandler.DefaultIssuer,
        ["azp"] = "halsa-backoffice",
        ["actor_tipo"] = "arl",
        ["tenant_id"] = tenant,
        ["permissions"] = new[] { "casos:read", "webhooks:manage" }
    });

    public static HttpClient ConClaims(ApiFactory factory, Dictionary<string, object> claims) =>
        factory.CreateClientWithHeaders(new Dictionary<string, string>
        {
            ["X-Test-Claims"] = JsonSerializer.Serialize(claims)
        });

    /// <summary>A REPS code no other test uses.</summary>
    public static string IpsNueva() =>
        "9" + RandomNumberGenerator.GetInt32(0, int.MaxValue).ToString("D11", CultureInfo.InvariantCulture)[..11];

    public static Task<HttpResponseMessage> CrearAsync(HttpClient client, string url, params string[] eventos) =>
        client.PostAsJsonAsync(Ruta, new { url, eventos });

    public static async Task<JsonObject> BodyAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>(IngestaClientes.JsonOptions))!;

    // ── SQL ───────────────────────────────────────────────────────────────────

    public static async Task<T?> EscalarAsync<T>(ApiFactory factory, string sql, params (string Nombre, object Valor)[] parametros)
    {
        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string nombre, object valor) in parametros)
        {
            cmd.Parameters.AddWithValue(nombre, valor);
        }

        object? valorLeido = await cmd.ExecuteScalarAsync();
        return valorLeido is null or DBNull ? default : (T)valorLeido;
    }

    public static async Task EjecutarAsync(ApiFactory factory, string sql, params (string Nombre, object Valor)[] parametros)
    {
        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string nombre, object valor) in parametros)
        {
            cmd.Parameters.AddWithValue(nombre, valor);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A delivery of <paramref name="suscripcion"/> in <paramref name="estado"/>, as the fan-out would write it.</summary>
    public static async Task<Guid> InsertEntregaAsync(ApiFactory factory, Guid suscripcion, string estado = "PENDIENTE")
    {
        Guid id = Guid.CreateVersion7();
        await EjecutarAsync(factory, """
            INSERT INTO integracion.entregas_webhook
              (id, suscripcion_id, evento_id, tipo, payload, estado, intentos, primer_intento_en, proximo_intento_en,
               entregada_en)
            VALUES (@id, @suscripcion, gen_random_uuid(), 'plazo.vencido', '{"tipo":"plazo.vencido"}', @estado, 3, now(), now(),
                    CASE WHEN @estado = 'ENTREGADA' THEN now() END)
            """, ("id", id), ("suscripcion", suscripcion), ("estado", estado));
        return id;
    }
}
