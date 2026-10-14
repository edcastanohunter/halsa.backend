using System.Text.Json;
using Halsa.Backend.Infrastructure.EventSourcing;
using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;

namespace Halsa.Backend.IntegrationTests.Terminos;

/// <summary>Delivering integration events and seeding the rows they refer to, for the SPEC 09 handlers.</summary>
internal static class EventosSql
{
    /// <summary>
    /// Delivers <paramref name="evento"/> through the in-process bus, exactly as the outbox processor would: one DI scope,
    /// every handler of the type.
    /// </summary>
    public static Task PublicarAsync(IServiceProvider services, IIntegrationEvent evento)
    {
        ArgumentNullException.ThrowIfNull(evento);

        return services.GetRequiredService<IEventBus>().PublishAsync(
            evento.GetType().FullName!,
            JsonSerializer.Serialize(evento, evento.GetType(), EventSerializer.Options));
    }

    /// <summary>An incapacidad row, as <c>halsa_owner</c>. <paramref name="cadenaId"/> null makes it an initial.</summary>
    public static async Task<Guid> InsertIncapacidadAsync(
        DatabaseFixture db,
        string tenantId,
        Guid casoId,
        DateOnly fechaInicio,
        int dias,
        int diasAcumulados,
        Guid? cadenaId = null,
        Guid? prorrogaDe = null)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenantId);

        Guid id = Guid.CreateVersion7();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO atel.incapacidades
              (id, tenant_id, caso_medico_id, numero_certificado, ips_emisora_reps, tipo, prorroga_de, cadena_id,
               fecha_inicio, dias, fecha_fin, dias_acumulados, modalidad, estado,
               profesional_tipo_documento, profesional_numero_documento, profesional_registro_rethus,
               diagnostico_categoria_indice, soporte_id, registro_clinico_id, registrada_en, registrada_por)
            VALUES
              (@id, @tid, @caso, @cert, '110011234501', @tipo, @prorroga, @cadena,
               @inicio, @dias, @inicio + (@dias - 1), @acumulados, 'PRESENCIAL', 'REGISTRADA',
               'CC', '12345678', 'RM-12345', '\x00'::bytea, gen_random_uuid(), gen_random_uuid(), now(), 'sub-test')
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("caso", casoId);
        cmd.Parameters.AddWithValue("cert", $"CERT-{id:N}"[..30]);
        cmd.Parameters.AddWithValue("tipo", cadenaId is null ? "INICIAL" : "PRORROGA");
        cmd.Parameters.AddWithValue("prorroga", (object?)prorrogaDe ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cadena", cadenaId ?? id);
        cmd.Parameters.AddWithValue("inicio", fechaInicio);
        cmd.Parameters.AddWithValue("dias", dias);
        cmd.Parameters.AddWithValue("acumulados", diasAcumulados);
        await cmd.ExecuteNonQueryAsync();

        return id;
    }
}
