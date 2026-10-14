using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Application.Plazos;
using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.Infrastructure.Tenancy;
using Halsa.Backend.SharedKernel.Primitives;
using Microsoft.Extensions.Logging;
using Npgsql;
using Quartz;

namespace Halsa.Backend.Infrastructure.Worker.Jobs;

/// <summary>Lo que hizo una pasada del barrido.</summary>
internal sealed record ResultadoBarrido(int Alertados, int Vencidos, int TopesCerrados);

/// <summary>
/// El barredor de plazos (SPEC 09, ARQ §3.4): un solo job, cada minuto, como <c>halsa_worker</c> sobre todos los
/// tenants. Toma lotes de <see cref="Lote"/> con <c>FOR UPDATE SKIP LOCKED</c>, como el outbox, así que dos instancias
/// del Worker nunca procesan el mismo plazo, y cada lote confirma su cambio y sus eventos en una transacción.
/// <list type="number">
///   <item>Abiertos con <c>alerta_en</c> cumplida y sin alertar: fija <c>alertado_en</c> y emite
///   <see cref="PlazoProximoAVencerIntegrationEvent"/>, una sola vez.</item>
///   <item>Abiertos con <c>vence_en</c> cumplido: pasan a <c>VENCIDO</c> y emiten <see cref="PlazoVencidoIntegrationEvent"/>.</item>
///   <item><c>TOPE_IT_180</c> abiertos de una cadena sin incapacidad nueva más de 30 días después de su último fin: pasan
///   a <c>CUMPLIDO</c> con la nota <c>"cadena cerrada antes del tope"</c>.</item>
/// </list>
/// Procesa todo lo atrasado, sin importar cuánto tiempo estuvo caído el Worker.
/// </summary>
[DisallowConcurrentExecution]
internal sealed partial class BarridoPlazosJob(BaseDatosWorker baseDatos, TimeProvider time, ILogger<BarridoPlazosJob> logger)
    : IJob
{
    public static readonly JobKey Key = new("barrido-plazos", TrabajosWorker.Grupo);

    public const int Lote = 500;

    private const string Devuelve = """
        RETURNING p.id, p.tenant_id, p.caso_medico_id, p.radicado, p.referencia_id, p.regla_codigo, p.vence_en,
                  (SELECT r.base_legal FROM sst.reglas_termino r WHERE r.id = p.regla_id)
        """;

    private const string AlertarSql = $"""
        WITH lote AS (
            SELECT id FROM sst.plazos
            WHERE estado = 'ABIERTO' AND alerta_en <= @ahora AND alertado_en IS NULL
            ORDER BY alerta_en
            LIMIT @lote
            FOR UPDATE SKIP LOCKED)
        UPDATE sst.plazos p SET alertado_en = @ahora
        FROM lote WHERE p.id = lote.id
        {Devuelve}
        """;

    private const string VencerSql = $"""
        WITH lote AS (
            SELECT id FROM sst.plazos
            WHERE estado = 'ABIERTO' AND vence_en <= @ahora
            ORDER BY vence_en
            LIMIT @lote
            FOR UPDATE SKIP LOCKED)
        UPDATE sst.plazos p SET estado = 'VENCIDO'
        FROM lote WHERE p.id = lote.id
        {Devuelve}
        """;

    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        await BarrerAsync(context.CancellationToken);
    }

    /// <summary>Una pasada completa: alertas, vencimientos y cierre de topes, lote tras lote hasta agotarlos.</summary>
    internal async Task<ResultadoBarrido> BarrerAsync(CancellationToken ct)
    {
        // Npgsql writes timestamptz only from UTC offsets.
        DateTimeOffset ahora = time.GetUtcNow().ToUniversalTime();

        int alertados = await PorLotesAsync(AlertarSql, ahora, Alerta, ct);
        int vencidos = await PorLotesAsync(VencerSql, ahora, Vencimiento, ct);
        int cerrados = await CerrarTopesAsync(ahora, ct);

        var resultado = new ResultadoBarrido(alertados, vencidos, cerrados);
        if (alertados + vencidos + cerrados > 0)
        {
            LogBarrido(logger, alertados, vencidos, cerrados);
        }

        return resultado;
    }

    private async Task<int> PorLotesAsync(
        string sql,
        DateTimeOffset ahora,
        Func<FilaBarrida, IIntegrationEvent> evento,
        CancellationToken ct)
    {
        int total = 0;
        int procesados;

        do
        {
            await using NpgsqlConnection conexion = await baseDatos.AbrirAsync(ct);
            await using NpgsqlTransaction transaccion = await conexion.BeginTransactionAsync(ct);

            List<FilaBarrida> filas = await ActualizarAsync(conexion, transaccion, sql, ahora, ct);
            await OutboxSql.EncolarAsync(conexion, transaccion, [.. filas.Select(evento)], ct);
            await transaccion.CommitAsync(ct);

            procesados = filas.Count;
            total += procesados;
        }
        while (procesados == Lote);

        return total;
    }

    private async Task<int> CerrarTopesAsync(DateTimeOffset ahora, CancellationToken ct)
    {
        DateOnly hoy = CalculadoraTerminos.FechaLocal(ahora);
        int total = 0;
        int procesados;

        do
        {
            await using NpgsqlConnection conexion = await baseDatos.AbrirAsync(ct);
            await using NpgsqlCommand cmd = conexion.CreateCommand();
            cmd.CommandText = """
                WITH lote AS (
                    SELECT id FROM sst.plazos
                    WHERE estado = 'ABIERTO' AND regla_codigo = @regla
                      AND ultima_fecha_fin IS NOT NULL AND ultima_fecha_fin + @dias < @hoy
                    LIMIT @lote
                    FOR UPDATE SKIP LOCKED)
                UPDATE sst.plazos p
                SET estado = 'CUMPLIDO', cumplido_en = @ahora, cumplido_por = @sistema, nota_cumplimiento = @nota
                FROM lote WHERE p.id = lote.id
                """;
            cmd.Parameters.AddWithValue("regla", CodigosRegla.TopeIt180);
            cmd.Parameters.AddWithValue("dias", Plazo.DiasCierreCadena);
            cmd.Parameters.AddWithValue("hoy", hoy);
            cmd.Parameters.AddWithValue("lote", Lote);
            cmd.Parameters.AddWithValue("ahora", ahora);
            cmd.Parameters.AddWithValue("sistema", SystemTenantScope.Sub);
            cmd.Parameters.AddWithValue("nota", Plazo.NotaCadenaCerrada);

            procesados = await cmd.ExecuteNonQueryAsync(ct);
            total += procesados;
        }
        while (procesados == Lote);

        return total;
    }

    private static async Task<List<FilaBarrida>> ActualizarAsync(
        NpgsqlConnection conexion,
        NpgsqlTransaction transaccion,
        string sql,
        DateTimeOffset ahora,
        CancellationToken ct)
    {
        await using NpgsqlCommand cmd = conexion.CreateCommand();
        cmd.Transaction = transaccion;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("ahora", ahora);
        cmd.Parameters.AddWithValue("lote", Lote);

        var filas = new List<FilaBarrida>();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            filas.Add(new FilaBarrida(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return filas;
    }

    private static PlazoProximoAVencerIntegrationEvent Alerta(FilaBarrida f) =>
        new(f.Id, f.TenantId, f.CasoMedicoId, f.Radicado, f.ReferenciaId, f.Resumen);

    private static PlazoVencidoIntegrationEvent Vencimiento(FilaBarrida f) =>
        new(f.Id, f.TenantId, f.CasoMedicoId, f.Radicado, f.ReferenciaId, f.Resumen);

    private sealed record FilaBarrida(
        Guid Id,
        string TenantId,
        Guid CasoMedicoId,
        string Radicado,
        Guid? ReferenciaId,
        string ReglaCodigo,
        DateTimeOffset VenceEn,
        string? BaseLegal)
    {
        public PlazoResumen Resumen => new(ReglaCodigo, BaseLegal, VenceEn);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Barrido de plazos: {Alertados} alertados, {Vencidos} vencidos, {Cerrados} topes cerrados")]
    private static partial void LogBarrido(ILogger logger, int alertados, int vencidos, int cerrados);
}
