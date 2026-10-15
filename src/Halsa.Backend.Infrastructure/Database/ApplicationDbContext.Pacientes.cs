using System.Data.Common;
using Halsa.Backend.Application.Abstractions.Pacientes;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Pacientes;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Halsa.Backend.Infrastructure.Database;

// SPEC 12: the writes of the patient portal. Each one opens its own transaction inside the execution strategy (the
// context retries transient failures), so a retried attempt starts from a clean change tracker: these commands track
// nothing else.
public sealed partial class ApplicationDbContext : IPacientesDbContext
{
    /// <summary>How many ids are drawn before giving up: 32⁶ of them, so a second collision is already unheard of.</summary>
    private const int SorteosIdCodigo = 3;

    async Task<CodigoEmitido> IPacientesDbContext.EmitirCodigoAsync(
        TenantId tenantId,
        CasoMedicoId casoId,
        Identificacion trabajador,
        string emitidoPor,
        DateTimeOffset ahora,
        Action<CodigoActivacion> registrar,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registrar);

        IExecutionStrategy strategy = Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async token =>
        {
            ChangeTracker.Clear();
            await using IDbContextTransaction transaccion = await Database.BeginTransactionAsync(token);

            // Serializes the emissions of the case: two at once would both find no ACTIVO code to expire.
            await Database.ExecuteSqlAsync(
                $"SELECT 1 FROM atel.casos_medicos WHERE id = {casoId.Value} FOR UPDATE", token);

            List<CodigoActivacion> activos = await CodigosActivacion
                .Where(c => c.CasoMedicoId == casoId && c.Estado == EstadoCodigoActivacion.Activo)
                .ToListAsync(token);
            activos.ForEach(c => c.Invalidar());
            await SaveChangesAsync(token);

            for (int sorteo = 1; ; sorteo++)
            {
                (CodigoActivacion codigo, string completo) = CodigoActivacion.Emitir(
                    tenantId, casoId, trabajador, emitidoPor, ahora);
                CodigosActivacion.Add(codigo);

                try
                {
                    await SaveChangesAsync(token);
                }
                catch (DbUpdateException ex) when (sorteo < SorteosIdCodigo && EsViolacion(ex, "pk_codigos_activacion"))
                {
                    // The id exists in another tenant, which the RLS hides. EF rolled back to its savepoint: draw again.
                    Entry(codigo).State = EntityState.Detached;
                    continue;
                }

                registrar(codigo);
                await SaveChangesAsync(token);
                await transaccion.CommitAsync(token);

                return new CodigoEmitido(codigo.Id, completo, codigo.ExpiraEn);
            }
        }, ct);
    }

    async Task<ResultadoCanje> IPacientesDbContext.CanjearCodigoAsync(
        string id,
        string secreto,
        string pacienteSub,
        Action<ResultadoCanje> registrar,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registrar);

        IExecutionStrategy strategy = Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async token =>
        {
            ChangeTracker.Clear();
            await using IDbContextTransaction transaccion = await Database.BeginTransactionAsync(token);

            ResultadoCanje resultado = await CanjearAsync(transaccion, id, secreto, pacienteSub, token);

            registrar(resultado);
            await SaveChangesAsync(token);
            await transaccion.CommitAsync(token);

            return resultado;
        }, ct);
    }

    /// <summary>
    /// Plain ADO.NET on the context's connection and transaction: the session variables are already set (the
    /// interceptor ran when the transaction opened it), and the row is not an entity.
    /// </summary>
    private async Task<ResultadoCanje> CanjearAsync(
        IDbContextTransaction transaccion,
        string id,
        string secreto,
        string pacienteSub,
        CancellationToken ct)
    {
        DbConnection conexion = Database.GetDbConnection();

        await using DbCommand comando = conexion.CreateCommand();
        comando.Transaction = transaccion.GetDbTransaction();
        comando.CommandText = """
            SELECT resultado, tenant_id, caso_medico_id, trabajador_tipo, trabajador_numero
            FROM atel.canjear_codigo_activacion(@id, @secreto, @sub)
            """;
        comando.Parameters.Add(new NpgsqlParameter("id", id));
        comando.Parameters.Add(new NpgsqlParameter("secreto", secreto));
        comando.Parameters.Add(new NpgsqlParameter("sub", pacienteSub));

        await using DbDataReader fila = await comando.ExecuteReaderAsync(ct);
        if (!await fila.ReadAsync(ct))
        {
            throw new InvalidOperationException("atel.canjear_codigo_activacion no devolvió ninguna fila.");
        }

        ResultadoCanjeTipo tipo = fila.GetString(0) switch
        {
            "VINCULADO" => ResultadoCanjeTipo.Vinculado,
            "INVALIDO" => ResultadoCanjeTipo.Invalido,
            "BLOQUEADO" => ResultadoCanjeTipo.Bloqueado,
            "DEMASIADOS_INTENTOS" => ResultadoCanjeTipo.DemasiadosIntentos,
            "VINCULO_DISTINTO" => ResultadoCanjeTipo.VinculoDistinto,
            string otro => throw new InvalidOperationException($"Resultado de canje desconocido: {otro}.")
        };

        TenantId? tenant = await fila.IsDBNullAsync(1, ct) ? null : new TenantId(fila.GetString(1));
        CasoMedicoId? caso = await fila.IsDBNullAsync(2, ct) ? null : new CasoMedicoId(fila.GetGuid(2));
        Identificacion? trabajador = await fila.IsDBNullAsync(3, ct)
            ? null
            : new Identificacion(fila.GetString(3), fila.GetString(4));

        return new ResultadoCanje(tipo, tenant, caso, trabajador);
    }

    void IPacientesDbContext.Agregar(SolicitudCopiaHc solicitud) => SolicitudesCopiaHc.Add(solicitud);

    Task<SolicitudCopiaHc?> IPacientesDbContext.SolicitudAsync(SolicitudCopiaHcId id, CancellationToken ct) =>
        SolicitudesCopiaHc.FirstOrDefaultAsync(s => s.Id == id, ct);

    async Task<bool> IPacientesDbContext.GuardarSolicitudAsync(CancellationToken ct)
    {
        try
        {
            await SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (EsViolacion(ex, "ux_solicitudes_copia_hc_caso_abierta"))
        {
            return false;
        }
    }

    private static bool EsViolacion(DbUpdateException ex, string restriccion) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg &&
        pg.ConstraintName == restriccion;
}
