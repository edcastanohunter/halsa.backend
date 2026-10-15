using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Casos.Commands.RevocarRemision;

internal sealed class RevocarRemisionCommandHandler(
    ICasoMedicoRepository repository,
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext,
    IAuditTrail auditTrail,
    IIntegrationEventPublisher publisher,
    TimeProvider time)
    : ICommandHandler<RevocarRemisionCommand>
{
    public async Task<Result> Handle(
        RevocarRemisionCommand command,
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

        Result result = caso.RevocarRemision(command.RemisionId, tenantContext.Sub, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        auditTrail.Registrar(
            AccionAuditoria.Actualizar,
            new ObjetivoAuditoria(RecursoAuditoria.Remision, command.RemisionId.ToString(), caso.Id.Value),
            caso.TenantId);

        // SPEC 10: the IPS that lost the case hears of it through its webhook.
        Remision remision = caso.Remisiones.First(r => r.Id == command.RemisionId);
        await publisher.EnqueueAsync(new RemisionRevocadaIntegrationEvent(
            caso.Id.Value,
            caso.TenantId.Value,
            caso.Radicado,
            remision.Id.Value,
            remision.IpsId.Value), cancellationToken);

        return await unitOfWork.GuardarCasoAsync(cancellationToken);
    }
}
