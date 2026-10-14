using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Casos.Commands.Desasignar;

internal sealed class DesasignarCasoCommandHandler(
    ICasoMedicoRepository repository,
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext,
    IAuditTrail auditTrail)
    : ICommandHandler<DesasignarCasoCommand>
{
    public async Task<Result> Handle(
        DesasignarCasoCommand command,
        CancellationToken cancellationToken)
    {
        if (!tenantContext.PuedeAsignar())
        {
            return Result.Failure(CasoMedicoErrors.ActorNoAutorizado);
        }

        CasoMedico? caso = await repository.GetByIdAsync(command.CasoId, cancellationToken);
        if (caso is null)
        {
            return Result.Failure(CasoMedicoErrors.NoEncontrado);
        }

        Result result = caso.Desasignar(command.Sub, tenantContext.IpsQueAsigna());
        if (result.IsFailure)
        {
            return result;
        }

        auditTrail.Registrar(
            AccionAuditoria.Eliminar,
            new ObjetivoAuditoria(RecursoAuditoria.Asignacion, command.Sub, caso.Id.Value),
            caso.TenantId);

        return await unitOfWork.GuardarCasoAsync(cancellationToken);
    }
}
