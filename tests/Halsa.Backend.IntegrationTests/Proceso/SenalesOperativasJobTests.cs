using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.Infrastructure.Worker;
using Halsa.Backend.Infrastructure.Worker.Jobs;
using Halsa.Backend.IntegrationTests.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Halsa.Backend.IntegrationTests.Proceso;

/// <summary>
/// SPEC 13, paso 9: las señales que Seq convierte en alertas, contadas como <c>halsa_worker</c>. Lo que se comprueba es
/// que el rol puede leerlas y que el criterio del outbox es <c>attempts &gt;= MaxAttempts</c> sin procesar.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SenalesOperativasJobTests(DatabaseFixture db)
{
    private const int MaxIntentos = 5;

    [Fact]
    public async Task ExhaustedOutboxMessages_ShouldBeCounted_AndPendingOrProcessedOnesShouldNot()
    {
        SenalesOperativas antes = await Job().RevisarAsync(CancellationToken.None);

        await InsertOutboxAsync(intentos: MaxIntentos, procesado: false);       // exhausted: counts
        await InsertOutboxAsync(intentos: MaxIntentos + 2, procesado: false);   // exhausted: counts
        await InsertOutboxAsync(intentos: MaxIntentos - 1, procesado: false);   // still retried
        await InsertOutboxAsync(intentos: MaxIntentos, procesado: true);        // processed in the end

        SenalesOperativas despues = await Job().RevisarAsync(CancellationToken.None);

        (despues.OutboxAgotados - antes.OutboxAgotados).ShouldBe(2);
    }

    [Fact]
    public async Task TheWorkerRole_ShouldReadEveryCountedTable()
    {
        SenalesOperativas senales = await Job().RevisarAsync(CancellationToken.None);

        senales.EntregasDeadLetter.ShouldBeGreaterThanOrEqualTo(0);
        senales.SuscripcionesSuspendidas.ShouldBeGreaterThanOrEqualTo(0);
    }

    private SenalesOperativasJob Job() => new(
        db.Services.GetRequiredService<BaseDatosWorker>(),
        Options.Create(new OutboxOptions { MaxAttempts = MaxIntentos }),
        TimeProvider.System,
        NullLogger<SenalesOperativasJob>.Instance);

    private async Task InsertOutboxAsync(int intentos, bool procesado)
    {
        await using var conn = new NpgsqlConnection(db.OwnerConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO outbox.outbox_messages (id, type, content, occurred_on_utc, processed_on_utc, attempts, error)
            VALUES (@id, 'Prueba.SenalesOperativas', '{}', now(), @procesado, @intentos, 'falló')
            """;
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("procesado", procesado ? DateTime.UtcNow : DBNull.Value);
        cmd.Parameters.AddWithValue("intentos", intentos);
        await cmd.ExecuteNonQueryAsync();
    }
}
