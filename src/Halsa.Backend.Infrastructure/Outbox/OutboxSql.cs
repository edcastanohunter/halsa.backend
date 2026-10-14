using Halsa.Backend.SharedKernel.Primitives;
using Npgsql;
using NpgsqlTypes;

namespace Halsa.Backend.Infrastructure.Outbox;

/// <summary>
/// Encola eventos en <c>outbox.outbox_messages</c> por SQL, dentro de la transacción de quien llama: para los barridos
/// del Worker (SPEC 09), que no usan EF. Las filas son las mismas que escribe <see cref="OutboxIntegrationEventPublisher"/>.
/// </summary>
internal static class OutboxSql
{
    public static async Task EncolarAsync(
        NpgsqlConnection conexion,
        NpgsqlTransaction transaccion,
        IReadOnlyCollection<IIntegrationEvent> eventos,
        CancellationToken ct)
    {
        if (eventos.Count == 0)
        {
            return;
        }

        OutboxMessage[] mensajes = [.. eventos.Select(OutboxMessage.Desde)];

        await using NpgsqlCommand cmd = conexion.CreateCommand();
        cmd.Transaction = transaccion;
        cmd.CommandText = """
            INSERT INTO outbox.outbox_messages (id, type, content, occurred_on_utc, attempts)
            SELECT m.id, m.type, m.content::jsonb, m.occurred_on_utc, 0
            FROM unnest(@ids, @tipos, @contenidos, @ocurridos) AS m (id, type, content, occurred_on_utc)
            """;
        cmd.Parameters.AddWithValue("ids", mensajes.Select(m => m.Id).ToArray());
        cmd.Parameters.AddWithValue("tipos", mensajes.Select(m => m.Type).ToArray());
        cmd.Parameters.AddWithValue("contenidos", mensajes.Select(m => m.Content).ToArray());
        cmd.Parameters.Add(new NpgsqlParameter("ocurridos", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
        {
            Value = mensajes.Select(m => DateTime.SpecifyKind(m.OccurredOnUtc, DateTimeKind.Utc)).ToArray()
        });

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
