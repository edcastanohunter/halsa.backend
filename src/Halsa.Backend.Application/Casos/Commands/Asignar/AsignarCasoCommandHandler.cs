using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Casos.Commands.Asignar;

internal sealed class AsignarCasoCommandHandler(
    ICasoMedicoRepository repository,
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext,
    IAuditTrail auditTrail,
    TimeProvider time)
    : ICommandHandler<AsignarCasoCommand>
{
    public async Task<Result> Handle(
        AsignarCasoCommand command,
        CancellationToken cancellationToken)
    {
        if (!tenantContext.PuedeEscribir())
        {
            return Result.Failure(CasoMedicoErrors.ActorNoAutorizado);
        }

        CasoMedico? caso = await repository.GetByIdAsync(command.CasoId, cancellationToken);
        if (caso is null)
        {
            return Result.Failure(CasoMedicoErrors.NoEncontrado);
        }

        caso.Asignar(command.Sub, tenantContext.Sub, time.GetUtcNow());

        // Audited even when the assignment already existed: the attempt is what matters.
        auditTrail.Registrar(
            AccionAuditoria.Crear,
            new ObjetivoAuditoria(RecursoAuditoria.Asignacion, command.Sub, caso.Id.Value),
            caso.TenantId);

        return await unitOfWork.GuardarCasoAsync(cancellationToken);
    }
}
