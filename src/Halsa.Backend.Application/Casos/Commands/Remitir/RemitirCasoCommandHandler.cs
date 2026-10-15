using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Casos.Commands.Remitir;

internal sealed class RemitirCasoCommandHandler(
    ICasoMedicoRepository repository,
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext,
    IAuditTrail auditTrail,
    IIntegrationEventPublisher publisher,
    TimeProvider time)
    : ICommandHandler<RemitirCasoCommand, Guid>
{
    public async Task<Result<Guid>> Handle(
        RemitirCasoCommand command,
        CancellationToken cancellationToken)
    {
        if (!tenantContext.PuedeEscribir())
        {
            return Result.Failure<Guid>(CasoMedicoErrors.ActorNoAutorizado);
        }

        CasoMedico? caso = await repository.GetByIdAsync(command.CasoId, cancellationToken);
        if (caso is null)
        {
            return Result.Failure<Guid>(CasoMedicoErrors.NoEncontrado);
        }

        // El validador ya exige 12 dígitos; esto solo construye el value object.
        if (!IpsId.TryCreate(command.IpsId, out IpsId ipsId))
        {
            return Result.Failure<Guid>(CasoMedicoErrors.IpsIdInvalido);
        }

        Result<RemisionId> result = caso.Remitir(ipsId, tenantContext.Sub, time.GetUtcNow());
        if (result.IsFailure)
        {
            return Result.Failure<Guid>(result.Error);
        }

        auditTrail.Registrar(
            AccionAuditoria.Crear,
            new ObjetivoAuditoria(RecursoAuditoria.Remision, result.Value.ToString(), caso.Id.Value),
            caso.TenantId);

        // SPEC 10: the IPS hears of it through its webhook. The worker's identification, never anything clinical.
        await publisher.EnqueueAsync(new CasoRemitidoIntegrationEvent(
            caso.Id.Value,
            caso.TenantId.Value,
            caso.Radicado,
            result.Value.Value,
            ipsId.Value,
            caso.TipoEvento.ToString(),
            caso.FechaEvento,
            caso.Trabajador.Tipo,
            caso.Trabajador.Numero), cancellationToken);

        Result guardado = await unitOfWork.GuardarCasoAsync(cancellationToken);
        return guardado.IsFailure
            ? Result.Failure<Guid>(guardado.Error)
            : Result.Success(result.Value.Value);
    }
}
