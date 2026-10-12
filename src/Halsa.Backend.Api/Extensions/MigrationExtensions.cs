using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Api.Extensions;

public static class MigrationExtensions
{
    /// <summary>
    /// Applies pending EF Core migrations using the <c>Migrations</c> connection string
    /// (role <c>halsa_owner</c>), which has the privileges required to create schemas,
    /// grant permissions and configure RLS.
    ///
    /// If the <c>Migrations</c> key is absent (e.g. in integration tests where the fixture
    /// owns the schema), falls back to the regular <c>Database</c> connection string via the
    /// default <see cref="ApplicationDbContext"/> registered in DI.
    ///
    /// In production, migrations are a deployment step — running them from the API on startup
    /// races between replicas and hides failures behind a health check.
    /// </summary>
    public static async Task ApplyMigrationsAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();

        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        string? migrationsConnectionString = configuration.GetConnectionString("Migrations");

        if (string.IsNullOrWhiteSpace(migrationsConnectionString))
        {
            // Fallback: use the registered ApplicationDbContext (works in tests).
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await context.Database.MigrateAsync();
            return;
        }

        // Build a one-off DbContext with the halsa_owner connection string.
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder
            .UseNpgsql(migrationsConnectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable(
                    Microsoft.EntityFrameworkCore.Migrations.HistoryRepository.DefaultTableName,
                    Schemas.Default);
            })
            .UseSnakeCaseNamingConvention();

        await using ApplicationDbContext migrationContext = new(optionsBuilder.Options, NullTenantContext.Instance);
        await migrationContext.Database.MigrateAsync();
    }
}
