namespace Halsa.Backend.Application.Abstractions.Auditoria;

/// <summary>
/// Una consulta cuya lectura se registra en <c>auditoria.accesos</c>. <c>AuditedQueryBehavior</c>
/// la audita y falla cerrado: si no puede registrar la lectura, no entrega los datos.
/// </summary>
public interface IAuditedQuery
{
    AccionAuditoria Accion { get; }

    ObjetivoAuditoria Objetivo { get; }
}
