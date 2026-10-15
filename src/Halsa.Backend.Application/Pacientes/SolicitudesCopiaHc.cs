using FluentValidation;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Pacientes;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Plazos;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.Domain.Pacientes;
using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Primitives;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Pacientes;

// Las solicitudes de copia de la HC (SPEC 12; Res. 1995/1999, art. 14): el paciente las crea sobre un caso que ve, la ARL
// del caso las atiende por el canal formal desde su bandeja. Cada una abre el plazo SOLICITUD_COPIA_HC (asíncrono, por
// el outbox) y su cierre lo cumple.

// ── Contratos ─────────────────────────────────────────────────────────────────

/// <summary>Una solicitud se creó: abre su plazo <c>SOLICITUD_COPIA_HC</c>. Solo tipos primitivos (contrato del outbox).</summary>
public sealed record SolicitudCopiaHcCreadaIntegrationEvent(
    Guid SolicitudId,
    string TenantId,
    Guid CasoMedicoId,
    DateTimeOffset SolicitadaEn) : IntegrationEvent;

/// <summary>El cuerpo de <c>POST /v1/paciente/casos/{id}/solicitudes-copia-hc</c>.</summary>
/// <param name="MedioEntrega"><c>CORREO_CERTIFICADO</c>, <c>PRESENCIAL</c> o <c>ELECTRONICO</c>.</param>
/// <param name="DireccionOCorreo">La dirección o el correo; no hace falta para <c>PRESENCIAL</c>.</param>
public sealed record CrearSolicitudCopiaHcRequest(string? MedioEntrega, string? DireccionOCorreo, string? Observaciones);

/// <summary>El cuerpo de <c>POST /v1/solicitudes-copia-hc/{id}/cerrar</c>.</summary>
/// <param name="Resultado"><c>ENTREGADA</c> o <c>RECHAZADA</c>.</param>
public sealed record CerrarSolicitudCopiaHcRequest(string? Resultado, string? Nota);

/// <summary>Una solicitud, como la ve el paciente que la hizo.</summary>
public sealed record SolicitudCopiaHcPacienteResponse(
    Guid Id,
    Guid CasoId,
    string MedioEntrega,
    string? DireccionOCorreo,
    string? Observaciones,
    string Estado,
    DateTimeOffset SolicitadaEn,
    DateTimeOffset? AtendidaEn,
    string? NotaAtencion);

/// <summary>Una solicitud de la bandeja de la ARL.</summary>
public sealed record SolicitudCopiaHcResponse(
    Guid Id,
    Guid CasoId,
    string Radicado,
    string PacienteSub,
    string MedioEntrega,
    string? DireccionOCorreo,
    string? Observaciones,
    string Estado,
    DateTimeOffset SolicitadaEn,
    DateTimeOffset? AtendidaEn,
    string? AtendidaPor,
    string? NotaAtencion);

public static class CodigosSolicitudCopiaHc
{
    public static readonly IReadOnlyList<string> Medios =
        [.. Enum.GetValues<MedioEntregaCopiaHc>().Select(m => CodigosDictamen.Codigo(m))];

    public static readonly IReadOnlyList<string> Estados =
        [.. Enum.GetValues<EstadoSolicitudCopiaHc>().Select(e => CodigosDictamen.Codigo(e))];

    /// <summary>La nota con que el cierre de la solicitud cumple su plazo.</summary>
    public static string NotaPlazo(EstadoSolicitudCopiaHc estado) =>
        $"Solicitud de copia de la HC {CodigosDictamen.Codigo(estado)}";

    internal static SolicitudCopiaHcPacienteResponse Paciente(SolicitudCopiaHc s) => new(
        s.Id.Value, s.CasoMedicoId.Value, CodigosDictamen.Codigo(s.MedioEntrega), s.DireccionOCorreo, s.Observaciones,
        CodigosDictamen.Codigo(s.Estado), s.SolicitadaEn, s.AtendidaEn, s.NotaAtencion);
}

internal static class SolicitudesCopiaHcActores
{
    /// <summary>La bandeja es del personal de la ARL del caso (con <c>solicitudes-hc:gestionar</c>).</summary>
    public static bool EsPersonalArl(this ITenantContext tenantContext) =>
        tenantContext.Resuelto && tenantContext.Tipo == ActorTipo.Arl && tenantContext.TenantId is not null;
}

// ── El paciente crea ──────────────────────────────────────────────────────────

/// <summary>
/// <c>POST /v1/paciente/casos/{id}/solicitudes-copia-hc</c>: <c>201</c>; <c>404</c> si el caso no es de un documento
/// vinculado; <c>409</c> si ya hay una abierta para el caso. Se audita como <c>CREAR / SOLICITUD_COPIA_HC</c> y encola el
/// evento que abre su plazo, todo en la misma transacción.
/// </summary>
public sealed record CrearSolicitudCopiaHcCommand(CasoMedicoId CasoId, CrearSolicitudCopiaHcRequest Request)
    : ICommand<SolicitudCopiaHcPacienteResponse>;

internal sealed class CrearSolicitudCopiaHcCommandValidator : AbstractValidator<CrearSolicitudCopiaHcCommand>
{
    public CrearSolicitudCopiaHcCommandValidator() =>
        RuleFor(c => c.Request).NotNull().ChildRules(request =>
        {
            request.RuleFor(r => r.MedioEntrega)
                .Must(m => m is not null && CodigosSolicitudCopiaHc.Medios.Contains(m))
                .WithErrorCode("SolicitudCopiaHc.MedioEntregaInvalido")
                .WithMessage("Debe ser CORREO_CERTIFICADO, PRESENCIAL o ELECTRONICO.");

            request.RuleFor(r => r.DireccionOCorreo)
                .NotEmpty()
                .When(r => r.MedioEntrega is "CORREO_CERTIFICADO" or "ELECTRONICO")
                .WithErrorCode("SolicitudCopiaHc.DireccionRequerida")
                .WithMessage("La dirección o el correo es obligatorio para este medio de entrega.");

            request.RuleFor(r => r.DireccionOCorreo)
                .EmailAddress()
                .When(r => r.MedioEntrega == "ELECTRONICO" && !string.IsNullOrWhiteSpace(r.DireccionOCorreo))
                .WithErrorCode("SolicitudCopiaHc.CorreoInvalido")
                .WithMessage("Debe ser un correo electrónico.");

            request.RuleFor(r => r.DireccionOCorreo)
                .MaximumLength(SolicitudCopiaHc.MaximoDireccion)
                .WithErrorCode("SolicitudCopiaHc.DireccionDemasiadoLarga");

            request.RuleFor(r => r.Observaciones)
                .MaximumLength(SolicitudCopiaHc.MaximoObservaciones)
                .WithErrorCode("SolicitudCopiaHc.ObservacionesDemasiadoLargas");
        });
}

internal sealed class CrearSolicitudCopiaHcCommandHandler(
    ITenantContext tenantContext,
    IReadDbContext readDb,
    IPacientesDbContext db,
    IAuditTrail auditTrail,
    IIntegrationEventPublisher publisher,
    TimeProvider time)
    : ICommandHandler<CrearSolicitudCopiaHcCommand, SolicitudCopiaHcPacienteResponse>
{
    public async Task<Result<SolicitudCopiaHcPacienteResponse>> Handle(
        CrearSolicitudCopiaHcCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!tenantContext.EsPaciente())
        {
            return Result.Failure<SolicitudCopiaHcPacienteResponse>(PacienteErrors.ActorNoAutorizado);
        }

        // Visible only through a link: an unlinked case answers as an absent one.
        TenantId? tenant = await readDb.CasosMedicos
            .AsNoTracking()
            .Where(c => c.Id == command.CasoId)
            .Select(c => (TenantId?)c.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant is null)
        {
            return Result.Failure<SolicitudCopiaHcPacienteResponse>(CasoMedicoErrors.NoEncontrado);
        }

        bool abierta = await readDb.SolicitudesCopiaHc.AnyAsync(
            s => s.CasoMedicoId == command.CasoId &&
                 (s.Estado == EstadoSolicitudCopiaHc.Recibida || s.Estado == EstadoSolicitudCopiaHc.EnTramite),
            cancellationToken);

        if (abierta)
        {
            return Result.Failure<SolicitudCopiaHcPacienteResponse>(SolicitudCopiaHcErrors.YaAbierta);
        }

        var solicitud = SolicitudCopiaHc.Crear(
            tenant.Value,
            command.CasoId,
            tenantContext.Sub,
            CodigosDictamen.Parse<MedioEntregaCopiaHc>(command.Request.MedioEntrega!),
            command.Request.DireccionOCorreo,
            command.Request.Observaciones,
            time.GetUtcNow());

        db.Agregar(solicitud);

        auditTrail.Registrar(
            AccionAuditoria.Crear,
            new ObjetivoAuditoria(RecursoAuditoria.SolicitudCopiaHc, solicitud.Id.Value.ToString(), command.CasoId.Value),
            tenant);

        await publisher.EnqueueAsync(new SolicitudCopiaHcCreadaIntegrationEvent(
            solicitud.Id.Value, tenant.Value.Value, command.CasoId.Value, solicitud.SolicitadaEn), cancellationToken);

        // Another account linked to the same document may have opened one this patient cannot see: the index answers.
        if (!await db.GuardarSolicitudAsync(cancellationToken))
        {
            return Result.Failure<SolicitudCopiaHcPacienteResponse>(SolicitudCopiaHcErrors.YaAbierta);
        }

        return CodigosSolicitudCopiaHc.Paciente(solicitud);
    }
}

// ── El paciente consulta las suyas ────────────────────────────────────────────

/// <summary><c>GET /v1/paciente/solicitudes-copia-hc</c>: las del paciente, de la más reciente a la más antigua.</summary>
public sealed record ListSolicitudesCopiaHcPacienteQuery : IQuery<IReadOnlyList<SolicitudCopiaHcPacienteResponse>>;

internal sealed class ListSolicitudesCopiaHcPacienteQueryHandler(ITenantContext tenantContext, IReadDbContext db)
    : IQueryHandler<ListSolicitudesCopiaHcPacienteQuery, IReadOnlyList<SolicitudCopiaHcPacienteResponse>>
{
    public async Task<Result<IReadOnlyList<SolicitudCopiaHcPacienteResponse>>> Handle(
        ListSolicitudesCopiaHcPacienteQuery query, CancellationToken cancellationToken)
    {
        if (!tenantContext.EsPaciente())
        {
            return Result.Failure<IReadOnlyList<SolicitudCopiaHcPacienteResponse>>(PacienteErrors.ActorNoAutorizado);
        }

        List<SolicitudCopiaHc> solicitudes = await db.SolicitudesCopiaHc
            .OrderByDescending(s => s.SolicitadaEn)
            .ToListAsync(cancellationToken);

        return solicitudes.Select(CodigosSolicitudCopiaHc.Paciente).ToList();
    }
}

// ── La bandeja de la ARL ──────────────────────────────────────────────────────

/// <summary>
/// <c>GET /v1/solicitudes-copia-hc?estado=</c>: las solicitudes del tenant, de la más antigua a la más reciente (el orden
/// en que corre su plazo). Se audita como <c>LEER / SOLICITUD_COPIA_HC</c>.
/// </summary>
public sealed record ListSolicitudesCopiaHcQuery(string? Estado)
    : IQuery<IReadOnlyList<SolicitudCopiaHcResponse>>, IAuditedQuery
{
    public AccionAuditoria Accion => AccionAuditoria.Leer;

    public ObjetivoAuditoria Objetivo => new(RecursoAuditoria.SolicitudCopiaHc, null, null);
}

internal sealed class ListSolicitudesCopiaHcQueryValidator : AbstractValidator<ListSolicitudesCopiaHcQuery>
{
    public ListSolicitudesCopiaHcQueryValidator() =>
        RuleFor(q => q.Estado)
            .Must(e => e is null || CodigosSolicitudCopiaHc.Estados.Contains(e))
            .WithErrorCode("SolicitudCopiaHc.EstadoInvalido")
            .WithMessage("Debe ser RECIBIDA, EN_TRAMITE, ENTREGADA o RECHAZADA.");
}

internal sealed class ListSolicitudesCopiaHcQueryHandler(ITenantContext tenantContext, IReadDbContext db, IAuditScope auditScope)
    : IQueryHandler<ListSolicitudesCopiaHcQuery, IReadOnlyList<SolicitudCopiaHcResponse>>
{
    public async Task<Result<IReadOnlyList<SolicitudCopiaHcResponse>>> Handle(
        ListSolicitudesCopiaHcQuery query, CancellationToken cancellationToken)
    {
        if (!tenantContext.EsPersonalArl())
        {
            return Result.Failure<IReadOnlyList<SolicitudCopiaHcResponse>>(PacienteErrors.ActorNoAutorizado);
        }

        auditScope.TenantDelRecurso(tenantContext.TenantId!.Value);

        IQueryable<SolicitudCopiaHc> solicitudes = db.SolicitudesCopiaHc;
        if (query.Estado is not null)
        {
            EstadoSolicitudCopiaHc estado = CodigosDictamen.Parse<EstadoSolicitudCopiaHc>(query.Estado);
            solicitudes = solicitudes.Where(s => s.Estado == estado);
        }

        var filas = await solicitudes
            .Join(db.CasosMedicos, s => s.CasoMedicoId, c => c.Id, (s, c) => new { Solicitud = s, c.Radicado })
            .OrderBy(x => x.Solicitud.SolicitadaEn)
            .ToListAsync(cancellationToken);

        return filas.Select(x => new SolicitudCopiaHcResponse(
            x.Solicitud.Id.Value,
            x.Solicitud.CasoMedicoId.Value,
            x.Radicado,
            x.Solicitud.PacienteSub,
            CodigosDictamen.Codigo(x.Solicitud.MedioEntrega),
            x.Solicitud.DireccionOCorreo,
            x.Solicitud.Observaciones,
            CodigosDictamen.Codigo(x.Solicitud.Estado),
            x.Solicitud.SolicitadaEn,
            x.Solicitud.AtendidaEn,
            x.Solicitud.AtendidaPor,
            x.Solicitud.NotaAtencion)).ToList();
    }
}

// ── Tramitar y cerrar ─────────────────────────────────────────────────────────

/// <summary>
/// <c>POST /v1/solicitudes-copia-hc/{id}/tramitar</c>: de <c>RECIBIDA</c> a <c>EN_TRAMITE</c>. <c>404</c> si no es del
/// tenant; <c>409</c> desde otro estado. Se audita como <c>ACTUALIZAR / SOLICITUD_COPIA_HC</c>.
/// </summary>
public sealed record TramitarSolicitudCopiaHcCommand(SolicitudCopiaHcId Id) : ICommand;

internal sealed class TramitarSolicitudCopiaHcCommandHandler(
    ITenantContext tenantContext,
    IPacientesDbContext db,
    IAuditTrail auditTrail,
    IUnitOfWork unitOfWork,
    TimeProvider time)
    : ICommandHandler<TramitarSolicitudCopiaHcCommand>
{
    public async Task<Result> Handle(TramitarSolicitudCopiaHcCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!tenantContext.EsPersonalArl())
        {
            return Result.Failure(PacienteErrors.ActorNoAutorizado);
        }

        SolicitudCopiaHc? solicitud = await db.SolicitudAsync(command.Id, cancellationToken);
        if (solicitud is null)
        {
            return Result.Failure(SolicitudCopiaHcErrors.NoEncontrada);
        }

        Result tramite = solicitud.Tramitar(tenantContext.Sub, time.GetUtcNow());
        if (tramite.IsFailure)
        {
            return tramite;
        }

        auditTrail.Registrar(
            AccionAuditoria.Actualizar,
            new ObjetivoAuditoria(RecursoAuditoria.SolicitudCopiaHc, solicitud.Id.Value.ToString(), solicitud.CasoMedicoId.Value),
            solicitud.TenantId);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>
/// <c>POST /v1/solicitudes-copia-hc/{id}/cerrar</c> — <c>{ resultado, nota }</c>: la cierra como <c>ENTREGADA</c> o
/// <c>RECHAZADA</c> y, en la misma transacción, cumple su plazo <c>SOLICITUD_COPIA_HC</c> (extemporáneo si ya venció). Si el
/// plazo aún no se abrió (el outbox no lo entregó), lo abre ya cumplido su handler. Se audita como
/// <c>ACTUALIZAR / SOLICITUD_COPIA_HC</c>.
/// </summary>
public sealed record CerrarSolicitudCopiaHcCommand(SolicitudCopiaHcId Id, CerrarSolicitudCopiaHcRequest Request) : ICommand;

internal sealed class CerrarSolicitudCopiaHcCommandValidator : AbstractValidator<CerrarSolicitudCopiaHcCommand>
{
    public CerrarSolicitudCopiaHcCommandValidator() =>
        RuleFor(c => c.Request).NotNull().ChildRules(request =>
        {
            request.RuleFor(r => r.Resultado)
                .Must(r => r is "ENTREGADA" or "RECHAZADA")
                .WithErrorCode("SolicitudCopiaHc.ResultadoInvalido")
                .WithMessage("Debe ser ENTREGADA o RECHAZADA.");

            request.RuleFor(r => r.Nota)
                .Must(n => n is not null && n.Trim().Length >= SolicitudCopiaHc.MinimoNota)
                .WithErrorCode("SolicitudCopiaHc.NotaRequerida")
                .WithMessage($"Debe tener al menos {SolicitudCopiaHc.MinimoNota} caracteres.")
                .MaximumLength(SolicitudCopiaHc.MaximoNota)
                .WithErrorCode("SolicitudCopiaHc.NotaDemasiadoLarga");
        });
}

internal sealed class CerrarSolicitudCopiaHcCommandHandler(
    ITenantContext tenantContext,
    IPacientesDbContext db,
    IPlazosRepository plazos,
    IAuditTrail auditTrail,
    IUnitOfWork unitOfWork,
    TimeProvider time)
    : ICommandHandler<CerrarSolicitudCopiaHcCommand>
{
    public async Task<Result> Handle(CerrarSolicitudCopiaHcCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!tenantContext.EsPersonalArl())
        {
            return Result.Failure(PacienteErrors.ActorNoAutorizado);
        }

        SolicitudCopiaHc? solicitud = await db.SolicitudAsync(command.Id, cancellationToken);
        if (solicitud is null)
        {
            return Result.Failure(SolicitudCopiaHcErrors.NoEncontrada);
        }

        DateTimeOffset ahora = time.GetUtcNow();
        var resultado = CodigosDictamen.Parse<EstadoSolicitudCopiaHc>(command.Request.Resultado!);

        Result cierre = solicitud.Cerrar(resultado, command.Request.Nota!.Trim(), tenantContext.Sub, ahora);
        if (cierre.IsFailure)
        {
            return cierre;
        }

        Plazo? plazo = (await plazos.DelCasoAsync(solicitud.CasoMedicoId, cancellationToken))
            .FirstOrDefault(p => p.ReglaCodigo == CodigosRegla.SolicitudCopiaHc && p.ReferenciaId == solicitud.Id.Value);

        // A plazo already fulfilled or suspended keeps its state: the request still closes.
        plazo?.Cumplir(CodigosSolicitudCopiaHc.NotaPlazo(resultado), ahora, tenantContext.Sub, ahora);

        auditTrail.Registrar(
            AccionAuditoria.Actualizar,
            new ObjetivoAuditoria(RecursoAuditoria.SolicitudCopiaHc, solicitud.Id.Value.ToString(), solicitud.CasoMedicoId.Value),
            solicitud.TenantId);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
