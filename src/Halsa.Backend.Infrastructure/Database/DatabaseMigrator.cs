using Halsa.Backend.Infrastructure.Database.Clinico;
using Halsa.Backend.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Halsa.Backend.Infrastructure.Database;

/// <summary>
/// Applies the migrations of both contexts with the owner connection string (<c>halsa_owner</c>).
/// <see cref="ApplicationDbContext"/> goes first: the clinical migrations depend on
/// <c>atel.tenant_del_caso_remitido</c>, which it creates.
///
/// Shared by the API (<c>ApplyMigrationsAsync</c>) and the test fixtures, so that both use exactly
/// the history tables that runtime registration uses.
/// </summary>
public static class DatabaseMigrator
{
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionString);

        DbContextOptions<ApplicationDbContext> applicationOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ownerConnectionString, ConfigureApplication)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var application = new ApplicationDbContext(applicationOptions, NullTenantContext.Instance))
        {
            await application.Database.MigrateAsync(cancellationToken);
        }

        DbContextOptions<ClinicalDbContext> clinicalOptions = new DbContextOptionsBuilder<ClinicalDbContext>()
            .UseNpgsql(ownerConnectionString, ConfigureClinical)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using var clinical = new ClinicalDbContext(clinicalOptions, NullTenantContext.Instance);
        await clinical.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>Monthly partitions of <c>auditoria.accesos</c> kept ahead of time after migrating (SPEC 04).</summary>
    public const int MesesDeParticionesAdelante = 3;

    /// <summary>
    /// Runs <c>auditoria.asegurar_particiones</c> as <c>halsa_owner</c>: the current month and the next
    /// <see cref="MesesDeParticionesAdelante"/>. Idempotent. Migrating calls it, and the Worker's daily
    /// <c>ParticionesAuditoriaJob</c> (SPEC 09) as <c>halsa_worker</c>.
    /// </summary>
    public static async Task AsegurarParticionesAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionString);

        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT auditoria.asegurar_particiones(@meses)";
        command.Parameters.AddWithValue("meses", MesesDeParticionesAdelante);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static void ConfigureApplication(NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, Schemas.Default);

    internal static void ConfigureClinical(NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable(ClinicalDbContext.MigrationsHistoryTable, Schemas.Clinico);
}
