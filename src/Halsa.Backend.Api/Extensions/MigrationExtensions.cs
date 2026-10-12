using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.Database.Clinico;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Api.Extensions;

public static class MigrationExtensions
{
    /// <summary>
    /// Applies pending EF Core migrations using the <c>Migrations</c> connection string
    /// (role <c>halsa_owner</c>), which has the privileges required to create schemas,
    /// grant permissions and configure RLS. <see cref="ApplicationDbContext"/> migrates first and
    /// <see cref="ClinicalDbContext"/> second, each with its own history table (SPEC 03).
    ///
    /// If the <c>Migrations</c> key is absent (e.g. in integration tests where the fixture
    /// owns the schema), falls back to the contexts registered in DI.
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
            // Fallback: use the registered contexts (works in tests).
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<ClinicalDbContext>().Database.MigrateAsync();
            return;
        }

        await DatabaseMigrator.MigrateAsync(migrationsConnectionString);
    }
}
