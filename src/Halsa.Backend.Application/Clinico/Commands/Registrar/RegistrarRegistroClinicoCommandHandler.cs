using Halsa.Backend.Application.Abstractions.Authentication;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Clinico.Commands.Registrar;

internal sealed class RegistrarRegistroClinicoCommandHandler(
    IClinicalCaseGate gate,
    IClinicalCipher cipher,
    IClinicalDbContext db,
    ICurrentUser currentUser,
    TimeProvider time)
    : ICommandHandler<RegistrarRegistroClinicoCommand, Guid>
{
    public async Task<Result<Guid>> Handle(
        RegistrarRegistroClinicoCommand command,
        CancellationToken cancellationToken)
    {
        // Invisible and missing answer the same, as on the administrative side.
        TenantId? tenant = await gate.TenantDelCasoVisibleAsync(command.CasoId, cancellationToken);
        if (tenant is null)
        {
            return Result.Failure<Guid>(ClinicoErrors.CasoNoEncontrado);
        }

        return await RegistroClinicoEscritura.RegistrarAsync(
            command, tenant.Value, corrigeA: null, cipher, db, currentUser, time, cancellationToken);
    }
}
