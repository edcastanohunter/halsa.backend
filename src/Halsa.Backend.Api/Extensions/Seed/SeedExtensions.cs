using System.Reflection;
using Halsa.Backend.Infrastructure.Tenancy;
using Halsa.Backend.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Halsa.Backend.Api.Extensions.Seed;

public static class SeedExtensions
{
    private const string SeedResource = "Halsa.Backend.Api.Extensions.Seed.casos-dev.sql";

    /// <summary>
    /// Executes <c>casos-dev.sql</c> against the database using the
    /// <c>Migrations</c> connection string (role <c>halsa_owner</c>). RLS is forced, so it
    /// applies to the owner too: the script sets <c>app.tenant_id</c> before each tenant's block.
    ///
    /// Only runs when <c>IWebHostEnvironment.IsDevelopment()</c> is <c>true</c>.
    /// Idempotent: the SQL uses <c>ON CONFLICT DO NOTHING</c>.
    /// </summary>
    public static async Task SeedDevDataAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!app.Environment.IsDevelopment())
        {
            return;
        }

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();

        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        string? connectionString = configuration.GetConnectionString("Migrations");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            app.Logger.LogWarning(
                "SeedDevDataAsync: 'Migrations' connection string not configured — skipping dev seed.");
            return;
        }

        string sql = ReadEmbeddedSql();

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();

        app.Logger.LogInformation("Development seed data applied (casos-dev.sql).");
    }

    private static string ReadEmbeddedSql()
    {
        Assembly assembly = typeof(SeedExtensions).Assembly;

        using Stream stream = assembly.GetManifestResourceStream(SeedResource)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{SeedResource}' not found in assembly '{assembly.FullName}'. " +
                "Check that the .sql file is marked as EmbeddedResource in the .csproj.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
