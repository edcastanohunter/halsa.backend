using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.Database.Auditoria;
using Halsa.Backend.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Halsa.Backend.IntegrationTests.Auditoria;

/// <summary>
/// Raw access to <c>auditoria.accesos</c> for the SPEC 04 tests. Assertions read as the superuser,
/// which bypasses RLS: rows with <c>tenant_id = null</c> are invisible to every application role.
/// </summary>
internal static class AuditoriaSql
{
    /// <summary>Every audit row of a case, read through the real EF mapping.</summary>
    public static async Task<List<AccesoAuditado>> AccesosDelCasoAsync(DatabaseFixture db, Guid casoId)
    {
        await using ApplicationDbContext context = db.CreateDbContext(NullTenantContext.Instance, db.SuperuserConnectionString);

        return await context.AccesosAuditados
            .AsNoTracking()
            .Where(a => a.CasoMedicoId == casoId)
            .OrderBy(a => a.OcurridoEn)
            .ToListAsync();
    }

    /// <summary>Inserts a minimal row as <c>halsa_owner</c>, which the insert policy allows.</summary>
    public static async Task<Guid> InsertAsOwnerAsync(DatabaseFixture db, DateTimeOffset ocurridoEn)
    {
        await using var conn = new NpgsqlConnection(db.OwnerConnectionString);
        await conn.OpenAsync();

        Guid id = Guid.CreateVersion7();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO auditoria.accesos
              (id, ocurrido_en, tenant_id, actor_id, actor_tipo, actor_roles, correlation_id,
               accion, resultado, recurso_tipo, hash_registro)
            VALUES
              (@id, @t, 'ARL_A', 'sub-test', 'arl', '{}', 'corr-test', 'LEER', 'EXITO', 'CASO', '\x00')
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("t", ocurridoEn.ToUniversalTime());
        await cmd.ExecuteNonQueryAsync();

        return id;
    }

    public static async Task<object?> ScalarAsSuperuserAsync(DatabaseFixture db, string sql, Guid id)
    {
        await using var conn = new NpgsqlConnection(db.SuperuserConnectionString);
        await conn.OpenAsync();

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync();
    }

    public static async Task ExecuteAsync(string connectionString, string sql, Guid? id = null)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (id is not null && sql.Contains("@id", StringComparison.Ordinal))
        {
            cmd.Parameters.AddWithValue("id", id.Value);
        }

        await cmd.ExecuteNonQueryAsync();
    }
}
