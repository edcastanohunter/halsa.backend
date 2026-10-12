using System.Data.Common;
using System.Globalization;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Integraciones;
using Halsa.Backend.Application.Integraciones.Registros;
using Halsa.Backend.Infrastructure.Auditoria;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Halsa.Backend.Infrastructure.Database.Integraciones;

/// <summary>
/// <see cref="IIdempotencyStore"/> sobre <see cref="IngestaDbContext"/>. La RLS de la tabla ya limita las
/// llaves al emisor de la sesión; el <c>WHERE</c> por emisor es la defensa en profundidad.
/// </summary>
internal sealed class IdempotencyStore(IngestaDbContext db) : IIdempotencyStore
{
    public Task<RespuestaIdempotente?> BuscarVigenteAsync(string emisor, Guid llave, DateTimeOffset ahora, CancellationToken ct) =>
        db.SolicitudesIdempotentes
            .AsNoTracking()
            .Where(s => s.Emisor == emisor && s.IdempotencyKey == llave && s.ExpiraEn > ahora)
            .Select(s => new RespuestaIdempotente(s.CuerpoSha256, s.SolicitudId, s.Respuesta))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// <c>INSERT ... ON CONFLICT DO UPDATE ... WHERE vencida</c>: una llave libre o vencida se toma (1 fila); una
    /// vigente no se toca (0 filas). Si otra transacción acaba de insertar la misma llave, PostgreSQL espera a
    /// que confirme y entonces la ve vigente: dos requests concurrentes con la misma llave no pueden ganar los dos.
    /// </summary>
    public async Task<bool> ReclamarAsync(SolicitudIdempotente solicitud, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(solicitud);

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("La llave se reclama dentro de la transacción de la radicación.");
        }

        int filas = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO integracion.solicitudes_idempotentes
              (emisor, idempotency_key, tenant_id, cuerpo_sha256, solicitud_id, respuesta, creada_en, expira_en)
            VALUES
              ({solicitud.Emisor}, {solicitud.IdempotencyKey}, {solicitud.TenantId}, {solicitud.CuerpoSha256},
               {solicitud.SolicitudId}, CAST({solicitud.Respuesta} AS jsonb), {solicitud.CreadaEn}, {solicitud.ExpiraEn})
            ON CONFLICT (emisor, idempotency_key) DO UPDATE SET
              tenant_id = EXCLUDED.tenant_id,
              cuerpo_sha256 = EXCLUDED.cuerpo_sha256,
              solicitud_id = EXCLUDED.solicitud_id,
              respuesta = EXCLUDED.respuesta,
              creada_en = EXCLUDED.creada_en,
              expira_en = EXCLUDED.expira_en
            WHERE solicitudes_idempotentes.expira_en <= EXCLUDED.creada_en
            """, ct);

        return filas == 1;
    }
}

/// <summary>
/// <see cref="IRadicadoGenerator"/> sobre <c>atel.secuencias_radicado</c>, en la conexión y la transacción de
/// <see cref="IngestaDbContext"/>. El <c>ON CONFLICT DO UPDATE</c> bloquea la fila del tenant y año hasta el fin
/// de la transacción, así que dos radicaciones del mismo tenant nunca reciben el mismo número.
/// </summary>
internal sealed class RadicadoGenerator(IngestaDbContext db) : IRadicadoGenerator
{
    private static readonly TimeSpan ZonaColombia = TimeSpan.FromHours(-5);

    public async Task<string> SiguienteAsync(TenantId tenant, DateTimeOffset ahora, CancellationToken ct)
    {
        IDbContextTransaction transaccion = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("El radicado se toma dentro de la transacción de la radicación.");

        int anio = ahora.ToOffset(ZonaColombia).Year;

        DbConnection conexion = db.Database.GetDbConnection();
        await using DbCommand comando = conexion.CreateCommand();
        comando.Transaction = transaccion.GetDbTransaction();
        comando.CommandText = """
            INSERT INTO atel.secuencias_radicado (tenant_id, anio, ultimo) VALUES (@tenant, @anio, 1)
            ON CONFLICT (tenant_id, anio) DO UPDATE SET ultimo = secuencias_radicado.ultimo + 1
            RETURNING ultimo
            """;

        DbParameter tenantParam = comando.CreateParameter();
        tenantParam.ParameterName = "tenant";
        tenantParam.Value = tenant.Value;
        comando.Parameters.Add(tenantParam);

        DbParameter anioParam = comando.CreateParameter();
        anioParam.ParameterName = "anio";
        anioParam.Value = anio;
        comando.Parameters.Add(anioParam);

        int numero = Convert.ToInt32(await comando.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);

        return Formato(tenant, anio, numero);
    }

    /// <summary><c>ATEL-{tenant}-{año}-{seq6}</c>. Con un tenant de 20 caracteres son 37, dentro de <c>varchar(40)</c>.</summary>
    internal static string Formato(TenantId tenant, int anio, int numero) =>
        string.Create(CultureInfo.InvariantCulture, $"ATEL-{tenant.Value}-{anio}-{numero:D6}");
}

/// <summary>Igual que <see cref="AuditTrail"/>, sobre <see cref="IngestaDbContext"/>: la fila cae en la transacción de la radicación.</summary>
internal sealed class IngestaAuditTrail(IngestaDbContext db, AccesoAuditadoFactory factory) : IIngestaAuditTrail
{
    public void Registrar(
        AccionAuditoria accion,
        ObjetivoAuditoria objetivo,
        TenantId tenant,
        ResultadoAuditoria resultado = ResultadoAuditoria.Exito) =>
        db.AccesosAuditados.Add(factory.Crear(accion, objetivo, tenant, resultado));
}
