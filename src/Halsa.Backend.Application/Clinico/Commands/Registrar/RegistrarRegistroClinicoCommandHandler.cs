using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Authentication;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Clinico.Commands.Registrar;

internal sealed class RegistrarRegistroClinicoCommandHandler(
    IClinicalCaseGate gate,
    IClinicalCipher cipher,
    IClinicalDbContext db,
    ICurrentUser currentUser,
    IClinicalAuditTrail auditTrail,
    ITenantContext tenantContext,
    TimeProvider time)
    : ICommandHandler<RegistrarRegistroClinicoCommand, RegistroClinicoCreadoResponse>
{
    public async Task<Result<RegistroClinicoCreadoResponse>> Handle(
        RegistrarRegistroClinicoCommand command,
        CancellationToken cancellationToken)
    {
        // Invisible and missing answer the same, as on the administrative side.
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

        return await RegistroClinicoEscritura.RegistrarAsync(
            command, caso.TenantId, corrigeA: null, AccionAuditoria.Crear, tenantContext.IpsId?.Value, cipher, db, auditTrail, currentUser, time,
            cancellationToken);
    }
}
