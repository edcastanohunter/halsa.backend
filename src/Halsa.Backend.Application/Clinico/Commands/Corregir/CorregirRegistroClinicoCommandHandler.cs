using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Authentication;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Clinico.Commands.Corregir;

internal sealed class CorregirRegistroClinicoCommandHandler(
    IClinicalCaseGate gate,
    IClinicalCipher cipher,
    IClinicalDbContext db,
    ICurrentUser currentUser,
    IClinicalAuditTrail auditTrail,
    ITenantContext tenantContext,
    TimeProvider time)
    : ICommandHandler<CorregirRegistroClinicoCommand, RegistroClinicoCreadoResponse>
{
    public async Task<Result<RegistroClinicoCreadoResponse>> Handle(
        CorregirRegistroClinicoCommand command,
        CancellationToken cancellationToken)
    {
        CasoVisible? caso = await gate.CasoVisibleAsync(command.CasoId, cancellationToken);
        if (caso is null)
        {
            return Result.Failure<RegistroClinicoCreadoResponse>(ClinicoErrors.CasoNoEncontrado);
        }

        Result admite = RegistroClinicoEscritura.AdmiteRegistro(caso, command.AtendidoEn);
        if (admite.IsFailure)
        {
            return Result.Failure<RegistroClinicoCreadoResponse>(admite.Error);
        }

        // The query filter and the clinical RLS decide visibility; the case must match too.
        bool existe = await db.Registros.AnyAsync(
            r => r.Id == command.RegistroId && r.CasoMedicoId == command.CasoId,
            cancellationToken);

        if (!existe)
        {
            return Result.Failure<RegistroClinicoCreadoResponse>(ClinicoErrors.RegistroNoEncontrado);
        }

        return await RegistroClinicoEscritura.RegistrarAsync(
            command, caso.TenantId, command.RegistroId, AccionAuditoria.Corregir, tenantContext.IpsId?.Value, cipher, db, auditTrail, currentUser, time,
            cancellationToken);
    }
}
