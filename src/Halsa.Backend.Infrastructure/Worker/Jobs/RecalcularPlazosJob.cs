using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.Infrastructure.Database.Configurations;
using Microsoft.Extensions.Logging;
using Npgsql;
using Quartz;

namespace Halsa.Backend.Infrastructure.Worker.Jobs;

/// <summary>
/// Recalcula los plazos <c>ABIERTO</c> con las reglas y los festivos actuales (SPEC 09, ARQ §3.4): diario, y cada vez
/// que <see cref="FestivosJob"/> carga festivos nuevos. Un plazo nunca se acorta: <c>vence_en = max(actual,
/// recalculado)</c>, y si se alarga, su alerta se recalcula desde el nuevo vencimiento.
///
/// <c>TOPE_IT_180</c> no se toca aquí: depende de los días acumulados de la cadena, no del calendario, y lo recalcula
/// cada incapacidad nueva.
/// </summary>
[DisallowConcurrentExecution]
internal sealed partial class RecalcularPlazosJob(BaseDatosWorker baseDatos, ILogger<RecalcularPlazosJob> logger) : IJob
{
    public static readonly JobKey Key = new("recalcular-plazos", TrabajosWorker.Grupo);

    public const int Lote = 500;

    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        await RecalcularAsync(context.CancellationToken);
    }

    /// <summary>Recorre todos los abiertos por lotes. Devuelve cuántos alargó.</summary>
    internal async Task<int> RecalcularAsync(CancellationToken ct)
    {
        await using NpgsqlConnection lectura = await baseDatos.AbrirAsync(ct);
        CalculadoraTerminos calculadora = new(await FestivosAsync(lectura, ct));
        ILookup<string, ReglaTermino> reglas = (await ReglasAsync(lectura, ct)).ToLookup(r => r.Codigo);

        int alargados = 0;
        Guid desde = Guid.Empty;

        while (true)
        {
            await using NpgsqlConnection conexion = await baseDatos.AbrirAsync(ct);
            await using NpgsqlTransaction transaccion = await conexion.BeginTransactionAsync(ct);

            List<Abierto> lote = await LoteAsync(conexion, transaccion, desde, ct);
            if (lote.Count == 0)
            {
                await transaccion.RollbackAsync(ct);
                break;
            }

            List<(Guid Id, Guid ReglaId, DateTimeOffset VenceEn, DateTimeOffset AlertaEn)> cambios = [];
            foreach (Abierto plazo in lote)
            {
                ReglaTermino? regla = reglas[plazo.ReglaCodigo]
                    .OrderByDescending(r => r.VigenteDesde)
                    .FirstOrDefault(r => r.VigenteEn(plazo.IniciaEn));
                if (regla is null)
                {
                    LogSinRegla(logger, plazo.Id, plazo.ReglaCodigo);
                    continue;
                }

                (DateTimeOffset recalculado, _) = regla.Calcular(calculadora, plazo.IniciaEn);
                if (recalculado > plazo.VenceEn)
                {
                    // Npgsql writes timestamptz only from UTC offsets.
                    cambios.Add((plazo.Id, regla.Id, recalculado.ToUniversalTime(),
                        calculadora.Alerta(recalculado, regla.AlertaCantidad, regla.AlertaUnidad).ToUniversalTime()));
                }
            }

            await ActualizarAsync(conexion, transaccion, cambios, ct);
            await transaccion.CommitAsync(ct);

            alargados += cambios.Count;
            desde = lote[^1].Id;
        }

        LogRecalculados(logger, alargados);
        return alargados;
    }

    private static async Task<List<DateOnly>> FestivosAsync(NpgsqlConnection conexion, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = conexion.CreateCommand();
        cmd.CommandText = "SELECT fecha FROM sst.festivos";

        var fechas = new List<DateOnly>();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            fechas.Add(reader.GetFieldValue<DateOnly>(0));
        }

        return fechas;
    }

    private static async Task<List<ReglaTermino>> ReglasAsync(NpgsqlConnection conexion, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = conexion.CreateCommand();
        cmd.CommandText = """
            SELECT id, codigo, evento_disparador, cantidad, unidad, alerta_cantidad, alerta_unidad, base_legal,
                   vigente_desde, vigente_hasta
            FROM sst.reglas_termino
            """;

        var reglas = new List<ReglaTermino>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            reglas.Add(new ReglaTermino(
                r.GetGuid(0), r.GetString(1), r.GetString(2),
                r.GetInt32(3), Unidad(r.GetString(4)), r.GetInt32(5), Unidad(r.GetString(6)),
                r.GetString(7), r.GetFieldValue<DateOnly>(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateOnly>(9)));
        }

        return reglas;
    }

    private static UnidadTermino Unidad(string codigo) =>
        (UnidadTermino)ReglaTerminoConfiguration.UnidadConverter.ConvertFromProvider(codigo)!;

    private static async Task<List<Abierto>> LoteAsync(
        NpgsqlConnection conexion, NpgsqlTransaction transaccion, Guid desde, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = conexion.CreateCommand();
        cmd.Transaction = transaccion;
        cmd.CommandText = """
            SELECT id, regla_codigo, inicia_en, vence_en
            FROM sst.plazos
            WHERE estado = 'ABIERTO' AND regla_codigo <> @tope AND id > @desde
            ORDER BY id
            LIMIT @lote
            FOR UPDATE SKIP LOCKED
            """;
        cmd.Parameters.AddWithValue("tope", CodigosRegla.TopeIt180);
        cmd.Parameters.AddWithValue("desde", desde);
        cmd.Parameters.AddWithValue("lote", Lote);

        var lote = new List<Abierto>();
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            lote.Add(new Abierto(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetFieldValue<DateTimeOffset>(3)));
        }

        return lote;
    }

    private static async Task ActualizarAsync(
        NpgsqlConnection conexion,
        NpgsqlTransaction transaccion,
        List<(Guid Id, Guid ReglaId, DateTimeOffset VenceEn, DateTimeOffset AlertaEn)> cambios,
        CancellationToken ct)
    {
        if (cambios.Count == 0)
        {
            return;
        }

        await using NpgsqlCommand cmd = conexion.CreateCommand();
        cmd.Transaction = transaccion;
        // The GREATEST is the database's own guarantee that nothing is shortened.
        cmd.CommandText = """
            UPDATE sst.plazos p
            SET regla_id = c.regla_id, vence_en = GREATEST(p.vence_en, c.vence_en), alerta_en = c.alerta_en
            FROM unnest(@ids, @reglas, @vencen, @alertan) AS c (id, regla_id, vence_en, alerta_en)
            WHERE p.id = c.id AND p.estado = 'ABIERTO'
            """;
        cmd.Parameters.AddWithValue("ids", cambios.Select(c => c.Id).ToArray());
        cmd.Parameters.AddWithValue("reglas", cambios.Select(c => c.ReglaId).ToArray());
        cmd.Parameters.AddWithValue("vencen", cambios.Select(c => c.VenceEn).ToArray());
        cmd.Parameters.AddWithValue("alertan", cambios.Select(c => c.AlertaEn).ToArray());

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed record Abierto(Guid Id, string ReglaCodigo, DateTimeOffset IniciaEn, DateTimeOffset VenceEn);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plazos recalculados: {Alargados} alargados")]
    private static partial void LogRecalculados(ILogger logger, int alargados);

    [LoggerMessage(Level = LogLevel.Warning, Message = "El plazo {PlazoId} no tiene una regla {Regla} vigente; no se recalcula")]
    private static partial void LogSinRegla(ILogger logger, Guid plazoId, string regla);
}
