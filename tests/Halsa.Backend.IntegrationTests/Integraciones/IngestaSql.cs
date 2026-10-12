using System.Text;
using System.Text.Json.Nodes;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Application.Integraciones.Canonico;
using Halsa.Backend.Application.Integraciones.Commands.Radicar;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Halsa.Backend.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;

namespace Halsa.Backend.IntegrationTests.Integraciones;

/// <summary>
/// Helpers for the SPEC 05 tests. Each test radicates in its own tenant, so counts never see another
/// test's rows. Reports go through the real command, exactly as the endpoint sends them.
/// </summary>
internal static class IngestaSql
{
    /// <summary>The ARL integration of <paramref name="tenant"/>, as <c>int-arl-{tenant}</c> would be resolved.</summary>
    public static FakeTenantContext Integracion(string tenant) =>
        new(ActorTipo.Integracion, new TenantId(tenant), null);

    /// <summary>The <c>FuratCaidaAltura</c> example with another report number and, optionally, other data.</summary>
    public static byte[] Furat(
        string numeroReporteOrigen,
        string? trabajadorNumero = null,
        DateTimeOffset? fechaHora = null)
    {
        JsonObject cuerpo = EjemplosContrato.Objeto(EjemplosContrato.FuratCaidaAltura);
        cuerpo["numeroReporteOrigen"] = numeroReporteOrigen;

        if (trabajadorNumero is not null)
        {
            cuerpo["trabajador"]!["identificacion"]!["numero"] = trabajadorNumero;
        }

        if (fechaHora is not null)
        {
            cuerpo["evento"]!["fechaHora"] = fechaHora.Value.ToString("O");
        }

        return Encoding.UTF8.GetBytes(cuerpo.ToJsonString());
    }

    public static byte[] Furep(string numeroReporteOrigen)
    {
        JsonObject cuerpo = EjemplosContrato.Objeto(EjemplosContrato.FurepAsma);
        cuerpo["numeroReporteOrigen"] = numeroReporteOrigen;
        return Encoding.UTF8.GetBytes(cuerpo.ToJsonString());
    }

    public static async Task<Result<RadicacionResponse>> RadicarAsync(
        DatabaseFixture db,
        string tenant,
        byte[] cuerpo,
        Guid? llave = null,
        TipoReporte tipo = TipoReporte.Furat)
    {
        await using AsyncServiceScope scope = db.CreateScope(Integracion(tenant));

        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new RadicarReporteCommand(
            tipo, (llave ?? Guid.NewGuid()).ToString(), cuerpo, $"int-arl-{tenant}", "correlacion-test"));
    }

    public static async Task<RadicacionResponse> RadicarValidoAsync(
        DatabaseFixture db, string tenant, byte[] cuerpo, Guid? llave = null, TipoReporte tipo = TipoReporte.Furat)
    {
        Result<RadicacionResponse> result = await RadicarAsync(db, tenant, cuerpo, llave, tipo);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    /// <summary><c>count(*)</c> as the <c>postgres</c> superuser, which bypasses RLS.</summary>
    public static async Task<long> ContarAsync(DatabaseFixture db, string sql, params (string Name, object Value)[] parametros)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.SuperuserConnectionString);
        return await ScalarAsync(conn, sql, parametros);
    }

    /// <summary>The rows a radication of <paramref name="tenant"/> leaves, table by table.</summary>
    public static async Task<Dictionary<string, long>> FilasDelTenantAsync(DatabaseFixture db, string tenant)
    {
        (string Name, object Value) t = ("tenant", tenant);

        return new Dictionary<string, long>
        {
            ["casos_medicos"] = await ContarAsync(db, "SELECT count(*) FROM atel.casos_medicos WHERE tenant_id = @tenant", t),
            ["reportes_evento"] = await ContarAsync(db, "SELECT count(*) FROM atel.reportes_evento WHERE tenant_id = @tenant", t),
            ["mensajes_entrantes"] = await ContarAsync(db, "SELECT count(*) FROM integracion.mensajes_entrantes WHERE tenant_id = @tenant", t),
            ["reporte_inicial"] = await ContarAsync(db,
                "SELECT count(*) FROM clinico.historia_clinica_detalle WHERE tenant_id = @tenant AND tipo = 'ReporteInicial'", t),
            ["solicitudes"] = await ContarAsync(db, "SELECT count(*) FROM integracion.solicitudes WHERE tenant_id = @tenant", t),
            ["solicitudes_idempotentes"] = await ContarAsync(db,
                "SELECT count(*) FROM integracion.solicitudes_idempotentes WHERE tenant_id = @tenant", t),
            ["outbox"] = await ContarAsync(db,
                "SELECT count(*) FROM outbox.outbox_messages WHERE content->>'tenantId' = @tenant", t),
            ["auditoria"] = await ContarAsync(db, "SELECT count(*) FROM auditoria.accesos WHERE tenant_id = @tenant", t)
        };
    }
}
