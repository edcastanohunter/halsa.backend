using FluentValidation;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Plazos;

/// <summary>El cuerpo de <c>POST /v1/casos/{id}/plazos/{plazoId}/cumplir</c> (SPEC 09).</summary>
/// <param name="CumplidoEn">Cuándo se cumplió. Sin valor: ahora.</param>
public sealed record CumplirPlazoRequest(string? Nota, DateTimeOffset? CumplidoEn);

/// <summary>
/// El cumplimiento manual de un plazo (SPEC 09), con una nota de al menos <see cref="Plazo.MinimoNota"/> caracteres. Solo
/// desde <c>ABIERTO</c> o <c>VENCIDO</c>; uno vencido cumplido tarde queda extemporáneo. Se audita como
/// <c>ACTUALIZAR / PLAZO</c>, en la misma transacción.
/// </summary>
public sealed record CumplirPlazoCommand(CasoMedicoId CasoId, PlazoId PlazoId, CumplirPlazoRequest Request) : ICommand;

internal sealed class CumplirPlazoCommandValidator : AbstractValidator<CumplirPlazoCommand>
{
    public CumplirPlazoCommandValidator() =>
        RuleFor(c => c.Request).NotNull().ChildRules(request =>
            request.RuleFor(r => r.Nota)
                .Must(n => n is not null && n.Trim().Length >= Plazo.MinimoNota)
                .WithErrorCode("Plazo.NotaRequerida")
                .WithMessage($"Debe tener al menos {Plazo.MinimoNota} caracteres.")
                .MaximumLength(Plazo.MaximoNota)
                .WithErrorCode("Plazo.NotaDemasiadoLarga")
                .WithMessage("Admite como máximo {MaxLength} caracteres."));
}

internal sealed class CumplirPlazoCommandHandler(
    ITenantContext tenantContext,
    IPlazosRepository plazos,
    IAuditTrail auditTrail,
    IUnitOfWork unitOfWork,
    TimeProvider time)
    : ICommandHandler<CumplirPlazoCommand>
{
    public async Task<Result> Handle(CumplirPlazoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!PlazosLectura.PuedeLeer(tenantContext))
        {
            return Result.Failure(PlazoErrors.ActorNoAutorizado);
        }

        // RLS limits it to the tenant's plazos: another tenant's answers as an absent one.
        Plazo? plazo = await plazos.GetAsync(command.CasoId, command.PlazoId, cancellationToken);
        if (plazo is null)
        {
            return Result.Failure(PlazoErrors.NoEncontrado);
        }

        DateTimeOffset ahora = time.GetUtcNow();
        Result cumplir = plazo.Cumplir(
            command.Request.Nota!.Trim(), command.Request.CumplidoEn ?? ahora, tenantContext.Sub, ahora);
        if (cumplir.IsFailure)
        {
            return cumplir;
        }

        auditTrail.Registrar(
            AccionAuditoria.Actualizar,
            new ObjetivoAuditoria(RecursoAuditoria.Plazo, plazo.Id.Value.ToString(), plazo.CasoMedicoId.Value),
            plazo.TenantId);

        // If the sweep expired it between the read and this save, fulfilling still wins: extemporaneo already compares
        // cumplidoEn with vence_en.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
