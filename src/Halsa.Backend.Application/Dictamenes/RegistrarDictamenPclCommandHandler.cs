using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Dictamenes;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore.Storage;

namespace Halsa.Backend.Application.Dictamenes;

/// <summary>
/// El registro del dictamen PCL (SPEC 08) en una transacción de <c>halsa_ingesta</c>, con el caso bloqueado: el
/// registro <c>DICTAMEN_PCL</c> de la HC (cifrado), la fila de <c>atel.dictamenes_pcl</c> (y el <c>corregido_por</c>
/// del anterior), la calificación del caso con su paso a <see cref="EstadoCaso.EnCalificacionPcl"/>, y la auditoría
/// (<c>CREAR</c> o <c>CORREGIR</c> sobre <c>REGISTRO_CLINICO</c>).
/// </summary>
internal sealed class RegistrarDictamenPclCommandHandler(
    ITenantContext tenantContext,
    IDictamenesDbContext db,
    IClinicalCipher cipher,
    IIngestaAuditTrail auditTrail,
    TimeProvider time)
    : ICommandHandler<RegistrarDictamenPclCommand, DictamenPclResponse>
{
    private static readonly TimeSpan ZonaColombia = TimeSpan.FromHours(-5);

    public async Task<Result<DictamenPclResponse>> Handle(RegistrarDictamenPclCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The dictamen is the ARL's: an IPS cannot write the case's calificación (and the RLS would refuse it too).
        if (!tenantContext.Resuelto || tenantContext.Tipo != ActorTipo.Arl)
        {
            return Result.Failure<DictamenPclResponse>(DictamenErrors.ActorNoAutorizado);
        }

        RegistrarDictamenPclRequest request = command.Request;
        DateTimeOffset ahora = time.GetUtcNow();

        await using IDbContextTransaction transaccion = await db.BeginTransactionAsync(cancellationToken);

        await db.BloquearCasoAsync(command.CasoId, cancellationToken);

        CasoMedico? caso = await db.CasoAsync(command.CasoId, cancellationToken);
        if (caso is null)
        {
            return Result.Failure<DictamenPclResponse>(ClinicoErrors.CasoNoEncontrado);
        }

        // One live dictamen per case; a correction names it, and only while it has not been notified.
        DictamenPcl? vigente = await db.VigenteAsync(command.CasoId, cancellationToken);
        Result regla = (vigente, request.CorrigeA) switch
        {
            (null, null) => Result.Success(),
            (null, not null) => Result.Failure(DictamenErrors.NoEncontrado),
            (not null, null) => Result.Failure(DictamenErrors.YaExiste),
            ({ EstadoNotificacion: not EstadoNotificacionDictamen.Pendiente }, _) => Result.Failure(DictamenErrors.YaNotificado),
            (not null, Guid corrigeA) when corrigeA != vigente.Id.Value => Result.Failure(DictamenErrors.CorrigeANoVigente),
            _ => Result.Success()
        };
        if (regla.IsFailure)
        {
            return Result.Failure<DictamenPclResponse>(regla.Error);
        }

        DictamenPclId id = DictamenPclId.New();
        OrigenPcl origen = CodigosDictamen.Parse<OrigenPcl>(request.Origen!);
        DateOnly fechaDictamen = request.FechaDictamen!.Value;
        DateOnly fechaEstructuracion = request.FechaEstructuracion!.Value;
        decimal porcentaje = request.PorcentajePcl!.Value;

        ContenidoClinico contenido = DictamenPclClinico.Contenido(
            request.Fundamentos!,
            [.. request.Diagnosticos!.Select(d => new CodigoClinico(d.Sistema!, d.Codigo!))],
            request.Componentes);

        Result<HistoriaClinicaDetalle> registro = await RegistroClinicoEscritura.CifrarAsync(
            caso.TenantId,
            caso.Id,
            TipoRegistroClinico.DictamenPcl,
            command.Calificador,
            // The clinical moment of a dictamen is its date, at 00:00 in Colombia.
            new DateTimeOffset(fechaDictamen.ToDateTime(TimeOnly.MinValue), ZonaColombia).ToUniversalTime(),
            contenido,
            vigente is null ? null : new HistoriaClinicaDetalleId(vigente.RegistroClinicoId),
            cipher,
            ahora,
            cancellationToken);
        if (registro.IsFailure)
        {
            return Result.Failure<DictamenPclResponse>(registro.Error);
        }

        Result<DictamenPcl> dictamen = DictamenPcl.Registrar(
            id, caso.TenantId, caso.Id, registro.Value.Id.Value, porcentaje, origen, fechaEstructuracion, fechaDictamen,
            command.Calificador.Sub, command.CalificadorNombre, command.Calificador.RegistroRethus!, vigente?.Id,
            DateOnly.FromDateTime(ahora.ToOffset(ZonaColombia).DateTime));
        if (dictamen.IsFailure)
        {
            return Result.Failure<DictamenPclResponse>(dictamen.Error);
        }

        Result calificar = caso.Calificar(
            new ResultadoCalificacion(porcentaje, fechaEstructuracion, CodigosDictamen.Codigo(origen)));
        if (calificar.IsFailure)
        {
            return Result.Failure<DictamenPclResponse>(calificar.Error);
        }

        if (vigente is not null)
        {
            Result corregir = vigente.CorregirCon(id);
            if (corregir.IsFailure)
            {
                return Result.Failure<DictamenPclResponse>(corregir.Error);
            }

            // First the old one stops being live (the FK to the new one is deferred to the commit), then the new one
            // goes in: the partial unique index never sees two live dictámenes.
            await db.GuardarAsync(cancellationToken);
        }

        db.Add(registro.Value);
        db.Add(dictamen.Value);
        auditTrail.Registrar(
            vigente is null ? AccionAuditoria.Crear : AccionAuditoria.Corregir,
            RegistroClinicoEscritura.Objetivo(registro.Value),
            caso.TenantId);

        await db.GuardarAsync(cancellationToken);
        await transaccion.CommitAsync(cancellationToken);

        return DictamenPclClinico.Respuesta(dictamen.Value, command.Calificador, contenido);
    }
}
