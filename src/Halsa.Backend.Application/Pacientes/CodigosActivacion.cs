using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Pacientes;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Pacientes;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Pacientes;

// ── La emisión ────────────────────────────────────────────────────────────────

/// <summary>
/// <c>POST /v1/casos/{id}/codigos-activacion</c> (SPEC 12): la ARL emite un código para el trabajador del caso. Solo para
/// un caso <c>Validado</c> o posterior; emitir otro vence el <c>ACTIVO</c> anterior. Se audita como
/// <c>CREAR / CODIGO_ACTIVACION</c>, en la misma transacción.
/// </summary>
public sealed record EmitirCodigoActivacionCommand(CasoMedicoId CasoId) : ICommand<CodigoActivacionResponse>;

internal sealed class EmitirCodigoActivacionCommandHandler(
    ITenantContext tenantContext,
    IReadDbContext readDb,
    IPacientesDbContext db,
    IAuditTrail auditTrail,
    TimeProvider time)
    : ICommandHandler<EmitirCodigoActivacionCommand, CodigoActivacionResponse>
{
    public async Task<Result<CodigoActivacionResponse>> Handle(
        EmitirCodigoActivacionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!tenantContext.EsDeArl())
        {
            return Result.Failure<CodigoActivacionResponse>(PacienteErrors.ActorNoAutorizado);
        }

        // RLS limits it to the tenant's cases: another tenant's answers as an absent one.
        var caso = await readDb.CasosMedicos
            .AsNoTracking()
            .Where(c => c.Id == command.CasoId)
            .Select(c => new { c.TenantId, c.Estado, c.Trabajador })
            .FirstOrDefaultAsync(cancellationToken);

        if (caso is null)
        {
            return Result.Failure<CodigoActivacionResponse>(CasoMedicoErrors.NoEncontrado);
        }

        if (!CodigoActivacion.AdmiteCodigos(caso.Estado))
        {
            return Result.Failure<CodigoActivacionResponse>(PacienteErrors.CasoNoValidado);
        }

        CodigoEmitido emitido = await db.EmitirCodigoAsync(
            caso.TenantId,
            command.CasoId,
            caso.Trabajador,
            tenantContext.Sub,
            time.GetUtcNow(),
            codigo => auditTrail.Registrar(
                AccionAuditoria.Crear,
                new ObjetivoAuditoria(RecursoAuditoria.CodigoActivacion, codigo.Id, command.CasoId.Value),
                caso.TenantId),
            cancellationToken);

        return new CodigoActivacionResponse(emitido.Codigo, emitido.ExpiraEn);
    }
}

// ── El canje ──────────────────────────────────────────────────────────────────

/// <summary>
/// <c>POST /v1/paciente/vinculos</c> (SPEC 12): el paciente canjea un código y su cuenta queda vinculada al documento del
/// trabajador. El mismo <see cref="PacienteErrors.CodigoInvalido"/> para un código inexistente, vencido, usado o mal
/// tecleado. Cada intento se audita como <c>CREAR / VINCULO</c> con su resultado.
/// </summary>
public sealed record CanjearCodigoActivacionCommand(CanjearCodigoRequest Request) : ICommand<VinculoCreadoResponse>;

internal sealed class CanjearCodigoActivacionCommandHandler(
    ITenantContext tenantContext,
    IPacientesDbContext db,
    IAuditTrail auditTrail,
    IUnitOfWork unitOfWork)
    : ICommandHandler<CanjearCodigoActivacionCommand, VinculoCreadoResponse>
{
    public async Task<Result<VinculoCreadoResponse>> Handle(
        CanjearCodigoActivacionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!tenantContext.EsPaciente())
        {
            return Result.Failure<VinculoCreadoResponse>(PacienteErrors.ActorNoAutorizado);
        }

        if (!FormatoCodigoActivacion.TryLeer(command.Request?.Codigo, out string? id, out string? secreto))
        {
            // Never reaches the function: nothing to count against the code, but the attempt is audited.
            auditTrail.Registrar(
                AccionAuditoria.Crear,
                new ObjetivoAuditoria(RecursoAuditoria.Vinculo, null, null),
                tenant: null,
                ResultadoAuditoria.Denegado);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Failure<VinculoCreadoResponse>(PacienteErrors.CodigoInvalido);
        }

        ResultadoCanje canje = await db.CanjearCodigoAsync(
            id,
            secreto,
            tenantContext.Sub,
            resultado => auditTrail.Registrar(
                AccionAuditoria.Crear,
                new ObjetivoAuditoria(RecursoAuditoria.Vinculo, id, resultado.CasoMedicoId?.Value),
                resultado.TenantId,
                resultado.Resultado switch
                {
                    ResultadoCanjeTipo.Vinculado => ResultadoAuditoria.Exito,
                    ResultadoCanjeTipo.VinculoDistinto => ResultadoAuditoria.Error,
                    _ => ResultadoAuditoria.Denegado
                }),
            cancellationToken);

        return canje.Resultado switch
        {
            ResultadoCanjeTipo.Vinculado => Result.Success(new VinculoCreadoResponse(new TrabajadorVinculadoResponse(
                canje.Trabajador!.Tipo, TrabajadorVinculadoResponse.Enmascarar(canje.Trabajador.Numero)))),
            ResultadoCanjeTipo.Bloqueado => Result.Failure<VinculoCreadoResponse>(PacienteErrors.CodigoBloqueado),
            ResultadoCanjeTipo.DemasiadosIntentos => Result.Failure<VinculoCreadoResponse>(PacienteErrors.DemasiadosIntentos),
            ResultadoCanjeTipo.VinculoDistinto => Result.Failure<VinculoCreadoResponse>(PacienteErrors.VinculoDistinto),
            _ => Result.Failure<VinculoCreadoResponse>(PacienteErrors.CodigoInvalido)
        };
    }
}
