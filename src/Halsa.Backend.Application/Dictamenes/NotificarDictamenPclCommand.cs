using FluentValidation;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Primitives;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Dictamenes;

/// <summary>El cuerpo de <c>POST /v1/casos/{casoId}/dictamen-pcl/notificar</c> (SPEC 08).</summary>
/// <param name="Medio"><c>CORREO_CERTIFICADO</c>, <c>ELECTRONICO</c> o <c>PERSONAL</c>.</param>
public sealed record NotificarDictamenPclRequest(DateTimeOffset? NotificadoEn, string? Medio);

/// <summary>Registra la notificación del dictamen vigente del caso. Dato administrativo: se escribe con <c>halsa_app</c>.</summary>
public sealed record NotificarDictamenPclCommand(CasoMedicoId CasoId, NotificarDictamenPclRequest Request) : ICommand;

/// <summary>
/// El dictamen PCL del caso quedó notificado (SPEC 08). De él colgará el plazo de controversia (SPEC 09). Solo datos
/// administrativos y tipos primitivos: es contrato público, y el outbox lo guarda en JSON por su nombre de tipo.
/// </summary>
/// <param name="Medio">El código del medio: <c>CORREO_CERTIFICADO</c>, <c>ELECTRONICO</c> o <c>PERSONAL</c>.</param>
/// <param name="Origen"><c>LABORAL</c>, <c>COMUN</c> o <c>MIXTO</c>.</param>
public sealed record DictamenPclNotificadoIntegrationEvent(
    Guid DictamenId,
    Guid CasoId,
    string TenantId,
    DateTimeOffset NotificadoEn,
    string Medio,
    DateOnly FechaDictamen,
    decimal PorcentajePcl,
    string Origen) : IntegrationEvent;

/// <summary>El dictamen rastreado por el contexto de <c>halsa_app</c>, que solo puede actualizar su notificación.</summary>
public interface IDictamenPclRepository
{
    /// <summary>El dictamen vigente del caso, si el actor lo ve; si no, null.</summary>
    Task<DictamenPcl?> VigenteAsync(CasoMedicoId casoId, CancellationToken ct);

    /// <summary>Un dictamen del caso por su id, si el actor lo ve; si no, null.</summary>
    Task<DictamenPcl?> GetAsync(CasoMedicoId casoId, DictamenPclId id, CancellationToken ct);
}

internal sealed class NotificarDictamenPclCommandValidator : AbstractValidator<NotificarDictamenPclCommand>
{
    public NotificarDictamenPclCommandValidator() =>
        RuleFor(c => c.Request).NotNull().ChildRules(request =>
        {
            request.RuleFor(r => r.NotificadoEn)
                .NotNull().WithErrorCode("Dictamen.NotificadoEnRequerido").WithMessage("Es obligatorio.");

            request.RuleFor(r => r.Medio)
                .Must(m => CodigosDictamen.TryParse<MedioNotificacion>(m, out _)).WithErrorCode("Dictamen.MedioInvalido")
                .WithMessage("Debe ser CORREO_CERTIFICADO, ELECTRONICO o PERSONAL.");
        });
}

internal sealed class NotificarDictamenPclCommandHandler(
    ITenantContext tenantContext,
    IDictamenPclRepository dictamenes,
    IIntegrationEventPublisher publisher,
    IAuditTrail auditTrail,
    IUnitOfWork unitOfWork,
    TimeProvider time)
    : ICommandHandler<NotificarDictamenPclCommand>
{
    public async Task<Result> Handle(NotificarDictamenPclCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!tenantContext.Resuelto || tenantContext.Tipo != ActorTipo.Arl)
        {
            return Result.Failure(DictamenErrors.ActorNoAutorizado);
        }

        DictamenPcl? dictamen = await dictamenes.VigenteAsync(command.CasoId, cancellationToken);
        if (dictamen is null)
        {
            return Result.Failure(DictamenErrors.NoEncontrado);
        }

        DateTimeOffset notificadoEn = command.Request.NotificadoEn!.Value;
        MedioNotificacion medio = CodigosDictamen.Parse<MedioNotificacion>(command.Request.Medio!);

        Result notificar = dictamen.Notificar(notificadoEn, medio, time.GetUtcNow());
        if (notificar.IsFailure)
        {
            return notificar;
        }

        await publisher.EnqueueAsync(new DictamenPclNotificadoIntegrationEvent(
            dictamen.Id.Value,
            dictamen.CasoMedicoId.Value,
            dictamen.TenantId.Value,
            notificadoEn,
            CodigosDictamen.Codigo(medio),
            dictamen.FechaDictamen,
            dictamen.PorcentajePcl,
            CodigosDictamen.Codigo(dictamen.Origen)), cancellationToken);

        // An administrative fact about the case: the clinical part of the dictamen is not touched.
        auditTrail.Registrar(
            AccionAuditoria.Actualizar,
            new ObjetivoAuditoria(RecursoAuditoria.Caso, dictamen.CasoMedicoId.Value.ToString(), dictamen.CasoMedicoId.Value),
            dictamen.TenantId);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
