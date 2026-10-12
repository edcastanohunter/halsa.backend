using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Abstractions.Auditoria;

/// <summary>
/// Escribe y confirma una fila de auditoría por su cuenta, fuera de la transacción del request.
/// Lo usan las lecturas (<c>AuditedQueryBehavior</c>) y los denegados de autorización, que no tienen
/// un cambio con el que confirmarse. Lanza si no pudo registrar.
/// </summary>
public interface IAuditWriter
{
    Task RegistrarAsync(
        AccionAuditoria accion,
        ObjetivoAuditoria objetivo,
        TenantId? tenant,
        ResultadoAuditoria resultado,
        CancellationToken cancellationToken);
}
