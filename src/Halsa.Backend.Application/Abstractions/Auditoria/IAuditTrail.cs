using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Abstractions.Auditoria;

/// <summary>
/// Auditoría de una escritura administrativa. Agrega la fila al <c>ApplicationDbContext</c> del request,
/// así que se confirma en el mismo <c>SaveChangesAsync</c> que el cambio: si la auditoría falla, el
/// cambio se revierte.
/// </summary>
public interface IAuditTrail
{
    /// <param name="tenant">Tenant del recurso, no del actor.</param>
    void Registrar(
        AccionAuditoria accion,
        ObjetivoAuditoria objetivo,
        TenantId? tenant,
        ResultadoAuditoria resultado = ResultadoAuditoria.Exito);
}

/// <summary>
/// Auditoría de una escritura clínica. Igual que <see cref="IAuditTrail"/>, pero sobre el
/// <c>ClinicalDbContext</c>: la fila cae en la transacción del registro clínico.
/// </summary>
public interface IClinicalAuditTrail
{
    /// <param name="tenant">Tenant del caso. En lo clínico siempre se conoce.</param>
    void Registrar(
        AccionAuditoria accion,
        ObjetivoAuditoria objetivo,
        TenantId tenant,
        ResultadoAuditoria resultado = ResultadoAuditoria.Exito);
}

/// <summary>
/// Auditoría de la radicación de una integración (SPEC 05). Igual que <see cref="IAuditTrail"/>, sobre el
/// contexto de la ingesta: las filas caen en la transacción que crea el caso y su reporte clínico.
/// </summary>
public interface IIngestaAuditTrail
{
    void Registrar(
        AccionAuditoria accion,
        ObjetivoAuditoria objetivo,
        TenantId tenant,
        ResultadoAuditoria resultado = ResultadoAuditoria.Exito);
}
