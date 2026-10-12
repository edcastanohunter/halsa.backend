using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Abstractions.Auditoria;

/// <summary>
/// Lo que el handler de una <see cref="IAuditedQuery"/> sabe y el behavior no: el tenant del recurso,
/// que no es el del actor (una IPS lee casos de varias ARL). Si el handler no lo informa, la fila
/// queda con <c>tenant_id = null</c>.
/// </summary>
public interface IAuditScope
{
    void TenantDelRecurso(TenantId tenant);
}
