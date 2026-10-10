using Halsa.Backend.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Api.Extensions;

public static class MigrationExtensions
{
    /// <summary>
    /// Convenience for local development only. In production, migrations are a deployment step:
    /// running them from the API races between replicas and hides failures behind a health check.
    /// </summary>
    public static async Task ApplyMigrationsAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await context.Database.MigrateAsync();
    }
}
