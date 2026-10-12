using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Authentication;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Clinico.Commands.Corregir;

internal sealed class CorregirRegistroClinicoCommandHandler(
    IClinicalCaseGate gate,
    IClinicalCipher cipher,
    IClinicalDbContext db,
    ICurrentUser currentUser,
    IClinicalAuditTrail auditTrail,
    TimeProvider time)
    : ICommandHandler<CorregirRegistroClinicoCommand, Guid>
{
    public async Task<Result<Guid>> Handle(
        CorregirRegistroClinicoCommand command,
        CancellationToken cancellationToken)
    {
        TenantId? tenant = await gate.TenantDelCasoVisibleAsync(command.CasoId, cancellationToken);
        if (tenant is null)
        {
            return Result.Failure<Guid>(ClinicoErrors.CasoNoEncontrado);
        }

        // The query filter and the clinical RLS decide visibility; the case must match too.
        bool existe = await db.Registros.AnyAsync(
            r => r.Id == command.RegistroId && r.CasoMedicoId == command.CasoId,
            cancellationToken);

        if (!existe)
        {
            return Result.Failure<Guid>(ClinicoErrors.RegistroNoEncontrado);
        }

        return await RegistroClinicoEscritura.RegistrarAsync(
            command, tenant.Value, command.RegistroId, AccionAuditoria.Corregir, cipher, db, auditTrail, currentUser, time,
            cancellationToken);
    }
}
