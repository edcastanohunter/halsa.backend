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
    TimeProvider time)
    : ICommandHandler<AsignarCasoCommand>
{
    public async Task<Result> Handle(
        AsignarCasoCommand command,
        CancellationToken cancellationToken)
    {
        CasoMedico? caso = await repository.GetByIdAsync(command.CasoId, cancellationToken);

        if (caso is null)
        {
            return Result.Failure(CasoMedicoErrors.NoEncontrado);
        }

        DateTimeOffset ahora = time.GetUtcNow();
        string asignadoPor = tenantContext.Resuelto ? tenantContext.Sub : "sistema";

        caso.Asignar(command.Sub, asignadoPor, ahora);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
