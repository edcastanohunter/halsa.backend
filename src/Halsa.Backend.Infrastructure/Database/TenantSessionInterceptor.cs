using System.Data.Common;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Halsa.Backend.Infrastructure.Database;

/// <summary>
/// Sets and clears the PostgreSQL session variables that the RLS policies read.
///
/// <list type="bullet">
///   <item>
///     On <see cref="ConnectionOpenedAsync"/>: writes <c>app.tenant_id</c>,
///     <c>app.ips_id</c>, <c>app.actor_tipo</c> and <c>app.sub</c> at session scope
///     so that every statement on this connection is filtered by the RLS policies.
///   </item>
///   <item>
///     On <see cref="ConnectionClosingAsync"/>: resets all four variables so the
///     pooled connection is safe to reuse by a different tenant.
///   </item>
/// </list>
///
/// The interceptor uses <c>set_config()</c> rather than a <c>SET</c> statement
/// so the values can be passed as real query parameters and never concatenated
/// into SQL.
/// </summary>
internal sealed class TenantSessionInterceptor(ITenantContext tenantContext) : DbConnectionInterceptor
{
    // ── Open ─────────────────────────────────────────────────────────────────

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (!tenantContext.Resuelto) return;

        SetSessionVars(connection);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (!tenantContext.Resuelto) return;

        await SetSessionVarsAsync(connection, cancellationToken);
    }

    // ── Close ─────────────────────────────────────────────────────────────────
    // Reset before the connection is returned to the pool so the next consumer
    // doesn't inherit the current tenant's session variables.

    public override InterceptionResult ConnectionClosing(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result)
    {
        if (tenantContext.Resuelto)
            ResetSessionVars(connection);

        return result;
    }

    public override async ValueTask<InterceptionResult> ConnectionClosingAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result)
    {
        if (tenantContext.Resuelto)
            await ResetSessionVarsAsync(connection, CancellationToken.None);

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetSessionVars(DbConnection connection)
    {
        using var cmd = BuildSetCommand(connection);
        cmd.ExecuteNonQuery();
    }

    private async Task SetSessionVarsAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = BuildSetCommand(connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private const string ResetSql =
        "SELECT set_config('app.tenant_id', '', false), set_config('app.ips_id', '', false), " +
        "set_config('app.actor_tipo', '', false), set_config('app.sub', '', false)";

    private void ResetSessionVars(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = ResetSql;
        cmd.ExecuteNonQuery();
    }

    private async Task ResetSessionVarsAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = ResetSql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private DbCommand BuildSetCommand(DbConnection connection)
    {
        string tenantId  = tenantContext.TenantId?.Value ?? string.Empty;
        string ipsId     = tenantContext.IpsId?.Value    ?? string.Empty;
        string actorTipo = tenantContext.Tipo.ToString().ToLowerInvariant();
        string sub       = tenantContext.Sub;

        // set_config(param, value, is_local) — is_local=false → session scope.
        // Values are passed as parameters so they cannot be injected into SQL.
        var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT set_config('app.tenant_id', @tid, false), set_config('app.ips_id', @ips, false), " +
            "set_config('app.actor_tipo', @actor, false), set_config('app.sub', @sub, false)";

        // Npgsql requires NpgsqlParameter; cast is safe because AddPersistence
        // configures UseNpgsql and the connection is always an NpgsqlConnection.
        cmd.Parameters.Add(new NpgsqlParameter("tid", tenantId));
        cmd.Parameters.Add(new NpgsqlParameter("ips", ipsId));
        cmd.Parameters.Add(new NpgsqlParameter("actor", actorTipo));
        cmd.Parameters.Add(new NpgsqlParameter("sub", sub));

        return cmd;
    }
}
