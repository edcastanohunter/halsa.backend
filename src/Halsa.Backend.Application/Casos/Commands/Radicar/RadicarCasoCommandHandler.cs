using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Casos.Commands.Radicar;

internal sealed class RadicarCasoCommandHandler(
    ICasoMedicoRepository repository,
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext,
    TimeProvider time)
    : ICommandHandler<RadicarCasoCommand, Guid>
{
    public async Task<Result<Guid>> Handle(
        RadicarCasoCommand command,
        CancellationToken cancellationToken)
    {
        if (!tenantContext.Resuelto || tenantContext.TenantId is null)
        {
            return Result.Failure<Guid>(CasoMedicoErrors.ActorNoAutorizado);
        }

        DateTimeOffset ahora = time.GetUtcNow();

        var result = CasoMedico.Radicar(
            tenantContext.TenantId.Value,
            command.Radicado,
            command.TipoEvento,
            new Identificacion(command.TrabajadorTipo, command.TrabajadorNumero),
            new Identificacion(command.EmpleadorTipo, command.EmpleadorNumero),
            command.FechaEvento,
            command.MunicipioDivipola,
            command.EsGraveOMortal,
            ahora);

        if (result.IsFailure)
        {
            return Result.Failure<Guid>(result.Error);
        }

        repository.Add(result.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(result.Value.Id.Value);
    }
}
