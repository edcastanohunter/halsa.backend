using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.Database.Clinico;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Infrastructure.Auditoria;

/// <summary>
/// Agrega la fila al <see cref="ApplicationDbContext"/> del request. No guarda: la fila se confirma
/// en el <c>SaveChangesAsync</c> del comando, junto con el cambio, o no se confirma.
/// </summary>
internal sealed class AuditTrail(ApplicationDbContext db, AccesoAuditadoFactory factory) : IAuditTrail
{
    public void Registrar(
        AccionAuditoria accion,
        ObjetivoAuditoria objetivo,
        TenantId? tenant,
        ResultadoAuditoria resultado = ResultadoAuditoria.Exito) =>
        db.AccesosAuditados.Add(factory.Crear(accion, objetivo, tenant, resultado));
}

/// <summary>
/// Igual que <see cref="AuditTrail"/>, sobre el <see cref="ClinicalDbContext"/>: la fila cae en la
/// transacción del registro clínico. <c>halsa_clinico</c> solo tiene <c>INSERT</c> sobre la tabla.
/// </summary>
internal sealed class ClinicalAuditTrail(ClinicalDbContext db, AccesoAuditadoFactory factory) : IClinicalAuditTrail
{
    public void Registrar(
        AccionAuditoria accion,
        ObjetivoAuditoria objetivo,
        TenantId tenant,
        ResultadoAuditoria resultado = ResultadoAuditoria.Exito) =>
        db.AccesosAuditados.Add(factory.Crear(accion, objetivo, tenant, resultado));
}
