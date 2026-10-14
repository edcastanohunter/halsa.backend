using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Halsa.Backend.Infrastructure.Auditoria;

/// <summary>
/// Confirma una fila en un <see cref="ApplicationDbContext"/> de un scope propio (rol <c>halsa_app</c>),
/// así no arrastra ni lo que el request tenga pendiente ni un contexto que ya falló. El actor se toma
/// del scope del request, antes de abrir el nuevo.
/// </summary>
internal sealed class AuditWriter(AccesoAuditadoFactory factory, IServiceScopeFactory scopeFactory) : IAuditWriter
{
    public async Task RegistrarAsync(
        AccionAuditoria accion,
        ObjetivoAuditoria objetivo,
        TenantId? tenant,
        ResultadoAuditoria resultado,
        CancellationToken cancellationToken,
        string? motivo = null)
    {
        var acceso = factory.Crear(accion, objetivo, tenant, resultado, motivo);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.AccesosAuditados.Add(acceso);
        await db.SaveChangesAsync(cancellationToken);
    }
}
