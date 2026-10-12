using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Halsa.Backend.SharedKernel.Tenancy;
using Npgsql;

namespace Halsa.Backend.Infrastructure.Clinico.Crypto;

/// <summary>Una DEK sin envolver y el id con el que se guarda junto a cada campo que cifra.</summary>
internal sealed record Dek(string KeyId, byte[] Material);

/// <summary>Las DEK por tenant y propósito. Separado del cifrador para poder probarlo sin base de datos.</summary>
internal interface IDekStore
{
    /// <summary>La DEK activa del tenant para el propósito. La genera la primera vez que se pide.</summary>
    Task<Dek> ActivaAsync(TenantId tenant, string proposito, CancellationToken ct);

    /// <summary>
    /// La DEK que cifró un campo. Lanza una <see cref="CryptographicException"/> si no existe o si es
    /// de otro tenant.
    /// </summary>
    Task<Dek> PorKeyIdAsync(TenantId tenant, string keyId, CancellationToken ct);
}

/// <summary>
/// Lee y genera las DEK de <c>clinico.llaves_tenant</c>, envueltas con la KEK. La primera vez que un
/// tenant necesita una, se genera con <c>INSERT ... ON CONFLICT DO NOTHING</c> y se relee: si dos
/// procesos compiten, gana una sola fila y los dos acaban usando la misma.
///
/// Las DEK sin envolver se cachean en memoria del proceso durante 10 minutos, nunca en Redis: eso
/// pondría llaves en claro en otro proceso.
///
/// Usa su propia fuente de conexiones (rol <c>halsa_clinico</c>) en vez del <c>ClinicalDbContext</c>:
/// es un singleton y la tabla no tiene RLS, así que no necesita las variables de sesión.
/// </summary>
internal sealed class DekStore(
    string clinicalConnectionString,
    IKeyEncryptionProvider kek,
    TimeProvider time)
    : IDekStore, IDisposable
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private const int DekBytes = 32;
    private const int VersionInicial = 1;

    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(clinicalConnectionString);
    private readonly ConcurrentDictionary<string, (Dek Dek, DateTimeOffset ExpiraEn)> _cache = new();

    public async Task<Dek> ActivaAsync(TenantId tenant, string proposito, CancellationToken ct)
    {
        string cacheKey = $"activa|{tenant.Value}|{proposito}";
        if (TryGetCached(cacheKey, out Dek? cached))
        {
            return cached;
        }

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        Fila? fila = await LeerActivaAsync(conn, tenant, proposito, ct);
        if (fila is null)
        {
            await InsertarAsync(conn, tenant, proposito, ct);

            // Re-read whatever won: our row, or the one a concurrent first use inserted.
            fila = await LeerActivaAsync(conn, tenant, proposito, ct)
                ?? throw new InvalidOperationException(
                    $"No se pudo generar la DEK {proposito} del tenant {tenant.Value}.");
        }

        Dek dek = Desenvolver(fila);
        Cache(cacheKey, dek);
        Cache($"key|{dek.KeyId}", dek);
        return dek;
    }

    public async Task<Dek> PorKeyIdAsync(TenantId tenant, string keyId, CancellationToken ct)
    {
        string cacheKey = $"key|{keyId}";
        if (!TryGetCached(cacheKey, out Dek? dek))
        {
            await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

            Fila fila = await LeerPorKeyIdAsync(conn, keyId, ct)
                ?? throw new CryptographicException($"La DEK '{keyId}' no existe.");

            dek = Desenvolver(fila);
            Cache(cacheKey, dek);
        }

        // Checked on every call, cached or not: a field must never be decrypted as another tenant's.
        if (!dek.KeyId.StartsWith($"{tenant.Value}:", StringComparison.Ordinal))
        {
            throw new CryptographicException($"La DEK '{keyId}' no pertenece al tenant {tenant.Value}.");
        }

        return dek;
    }

    public void Dispose() => _dataSource.Dispose();

    // ── SQL ───────────────────────────────────────────────────────────────────

    private sealed record Fila(string KeyId, string TenantId, string Proposito, string KekId, byte[] DekEnvuelta);

    private const string Columnas = "key_id, tenant_id, proposito, kek_id, dek_envuelta";

    private static async Task<Fila?> LeerActivaAsync(
        NpgsqlConnection conn, TenantId tenant, string proposito, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columnas} FROM clinico.llaves_tenant
            WHERE tenant_id = @tenant AND proposito = @proposito AND activa
            """;
        cmd.Parameters.AddWithValue("tenant", tenant.Value);
        cmd.Parameters.AddWithValue("proposito", proposito);
        return await LeerFilaAsync(cmd, ct);
    }

    private static async Task<Fila?> LeerPorKeyIdAsync(NpgsqlConnection conn, string keyId, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columnas} FROM clinico.llaves_tenant WHERE key_id = @key_id";
        cmd.Parameters.AddWithValue("key_id", keyId);
        return await LeerFilaAsync(cmd, ct);
    }

    private static async Task<Fila?> LeerFilaAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new Fila(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<byte[]>(4));
    }

    private async Task InsertarAsync(NpgsqlConnection conn, TenantId tenant, string proposito, CancellationToken ct)
    {
        string keyId = $"{tenant.Value}:{proposito}:v{VersionInicial}";
        byte[] material = RandomNumberGenerator.GetBytes(DekBytes);

        try
        {
            byte[] envuelta = kek.Envolver(material, Aad(tenant.Value, proposito, keyId));

            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO clinico.llaves_tenant
                  (key_id, tenant_id, proposito, version, kek_id, dek_envuelta, creada_en, activa)
                VALUES (@key_id, @tenant, @proposito, @version, @kek_id, @dek, @creada_en, true)
                ON CONFLICT DO NOTHING
                """;
            cmd.Parameters.AddWithValue("key_id", keyId);
            cmd.Parameters.AddWithValue("tenant", tenant.Value);
            cmd.Parameters.AddWithValue("proposito", proposito);
            cmd.Parameters.AddWithValue("version", VersionInicial);
            cmd.Parameters.AddWithValue("kek_id", kek.KekId);
            cmd.Parameters.AddWithValue("dek", envuelta);
            cmd.Parameters.AddWithValue("creada_en", time.GetUtcNow());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            // The winner is re-read and unwrapped; this copy is either stored wrapped or discarded.
            CryptographicOperations.ZeroMemory(material);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Dek Desenvolver(Fila fila)
    {
        if (!string.Equals(fila.KekId, kek.KekId, StringComparison.Ordinal))
        {
            throw new CryptographicException(
                $"La DEK '{fila.KeyId}' está envuelta con la KEK '{fila.KekId}', no con la configurada ('{kek.KekId}').");
        }

        byte[] material = kek.Desenvolver(fila.DekEnvuelta, Aad(fila.TenantId, fila.Proposito, fila.KeyId));
        return new Dek(fila.KeyId, material);
    }

    /// <summary>AAD de la envoltura: <c>"{tenantId}|{proposito}|{keyId}"</c>.</summary>
    private static byte[] Aad(string tenantId, string proposito, string keyId) =>
        Encoding.UTF8.GetBytes($"{tenantId}|{proposito}|{keyId}");

    private bool TryGetCached(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Dek? dek)
    {
        if (_cache.TryGetValue(key, out (Dek Dek, DateTimeOffset ExpiraEn) entry) && entry.ExpiraEn > time.GetUtcNow())
        {
            dek = entry.Dek;
            return true;
        }

        _cache.TryRemove(key, out _);
        dek = null;
        return false;
    }

    private void Cache(string key, Dek dek) => _cache[key] = (dek, time.GetUtcNow().Add(CacheTtl));
}
