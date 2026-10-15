using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Incapacidades;
using Halsa.Backend.Application.Plazos;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.Domain.Incapacidades;
using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Pacientes;

// Las consultas del portal del paciente (SPEC 12). Ninguna recibe ids de trabajador: lo que el paciente ve lo deciden su
// app.sub, los vínculos y la RLS (y el query filter, que la refleja). Solo hitos administrativos: ni diagnósticos, ni
// porcentajes, ni fundamentos (ARQ §4.3). Un paciente sin vínculo recibe listas vacías.

// ── Contratos ─────────────────────────────────────────────────────────────────

/// <summary>Un caso de <c>GET /v1/paciente/mis-casos</c> y de <c>casosActivos</c> del resumen.</summary>
/// <param name="Arl">El código de la ARL del caso (su tenant).</param>
/// <param name="UltimoHitoEn">La fecha del hito más reciente de su línea de tiempo.</param>
public sealed record CasoPacienteResponse(
    Guid CasoId,
    string Radicado,
    string TipoEvento,
    string Estado,
    DateTimeOffset FechaEvento,
    string Arl,
    DateTimeOffset UltimoHitoEn);

/// <param name="Tipo">
/// <c>RADICADO</c>, <c>VALIDADO</c>, <c>RECHAZADO</c>, <c>REMITIDO_A_IPS</c>, <c>INCAPACIDAD_REGISTRADA</c>,
/// <c>DICTAMEN_NOTIFICADO</c> o <c>DICTAMEN_EN_FIRME</c>.
/// </param>
public sealed record HitoResponse(string Tipo, DateTimeOffset Fecha, string Descripcion);

/// <summary>Una incapacidad de <c>GET /v1/paciente/incapacidades</c>: la parte administrativa, sin diagnóstico.</summary>
public sealed record IncapacidadPacienteResponse(
    Guid Id,
    Guid CasoId,
    string Tipo,
    DateOnly FechaInicio,
    DateOnly FechaFin,
    int Dias,
    int DiasAcumulados,
    string Estado);

public sealed record IncapacidadVigenteResponse(Guid CasoId, DateOnly FechaInicio, DateOnly FechaFin, int Dias);

/// <param name="Tipo"><c>FIN_INCAPACIDAD</c> o <c>VENCE_CONTROVERSIA</c>.</param>
public sealed record ProximoHitoResponse(Guid CasoId, string Tipo, DateOnly Fecha);

public sealed record ResumenPacienteResponse(
    IReadOnlyList<CasoPacienteResponse> CasosActivos,
    IReadOnlyList<IncapacidadVigenteResponse> IncapacidadesVigentes,
    IReadOnlyList<ProximoHitoResponse> ProximosHitos);

public static class TiposHito
{
    public const string Radicado = "RADICADO";
    public const string Validado = "VALIDADO";
    public const string Rechazado = "RECHAZADO";
    public const string RemitidoAIps = "REMITIDO_A_IPS";
    public const string IncapacidadRegistrada = "INCAPACIDAD_REGISTRADA";
    public const string DictamenNotificado = "DICTAMEN_NOTIFICADO";
    public const string DictamenEnFirme = "DICTAMEN_EN_FIRME";

    public const string FinIncapacidad = "FIN_INCAPACIDAD";
    public const string VenceControversia = "VENCE_CONTROVERSIA";
}

// ── La lectura común ──────────────────────────────────────────────────────────

/// <summary>Un caso visible al paciente, con lo que su línea de tiempo necesita.</summary>
internal sealed record CasoDelPaciente(
    CasoMedico Caso,
    IReadOnlyList<Incapacidad> Incapacidades,
    IReadOnlyList<DictamenNotificado> Dictamenes)
{
    public bool Activo => Caso.Estado is not (EstadoCaso.Cerrado or EstadoCaso.Rechazado);
}

/// <param name="VenceControversiaEn">Calculado con la regla vigente: el paciente no ve los plazos (son del tenant).</param>
internal sealed record DictamenNotificado(
    DictamenPclId Id,
    DateTimeOffset NotificadoEn,
    EstadoNotificacionDictamen Estado,
    DateTimeOffset VenceControversiaEn);

internal sealed class PortalPaciente(IReadDbContext db, ICatalogoTerminos catalogo)
{
    /// <summary>Los casos visibles (todos, o solo <paramref name="casoId"/>), con sus remisiones, incapacidades y dictámenes.</summary>
    public async Task<List<CasoDelPaciente>> CasosAsync(CasoMedicoId? casoId, CancellationToken ct)
    {
        IQueryable<CasoMedico> consulta = db.CasosMedicos.AsNoTracking().Include(c => c.Remisiones);
        if (casoId is { } id)
        {
            consulta = consulta.Where(c => c.Id == id);
        }

        List<CasoMedico> casos = await consulta.OrderByDescending(c => c.RadicadoEn).ToListAsync(ct);
        if (casos.Count == 0)
        {
            return [];
        }

        CasoMedicoId[] ids = [.. casos.Select(c => c.Id)];

        List<Incapacidad> incapacidades = await db.Incapacidades
            .Where(i => ids.Contains(i.CasoMedicoId))
            .OrderBy(i => i.FechaInicio)
            .ToListAsync(ct);

        List<DictamenPcl> dictamenes = await db.DictamenesPcl
            .Where(d => ids.Contains(d.CasoMedicoId) && d.NotificadoEn != null)
            .ToListAsync(ct);

        List<(CasoMedicoId Caso, DictamenNotificado Dictamen)> notificados = [];
        if (dictamenes.Count > 0)
        {
            CalculadoraTerminos calculadora = await catalogo.CalculadoraAsync(ct);
            foreach (DictamenPcl dictamen in dictamenes)
            {
                DateTimeOffset notificadoEn = dictamen.NotificadoEn!.Value;
                ReglaTermino regla = await catalogo.ReglaVigenteAsync(CodigosRegla.ControversiaDictamen, notificadoEn, ct);
                (DateTimeOffset vence, _) = regla.Calcular(calculadora, notificadoEn);

                notificados.Add((dictamen.CasoMedicoId,
                    new DictamenNotificado(dictamen.Id, notificadoEn, dictamen.EstadoNotificacion, vence)));
            }
        }

        return [.. casos.Select(c => new CasoDelPaciente(
            c,
            [.. incapacidades.Where(i => i.CasoMedicoId == c.Id)],
            [.. notificados.Where(n => n.Caso == c.Id).Select(n => n.Dictamen)]))];
    }

    /// <summary>Los hitos administrativos del caso, del más antiguo al más reciente.</summary>
    public static List<HitoResponse> LineaTiempo(CasoDelPaciente c)
    {
        CasoMedico caso = c.Caso;
        var hitos = new List<HitoResponse>
        {
            new(TiposHito.Radicado, caso.RadicadoEn, $"Se radicó el reporte del evento con el número {caso.Radicado}.")
        };

        if (caso.ValidadoEn is { } validadoEn)
        {
            hitos.Add(new(TiposHito.Validado, validadoEn, "La ARL validó la radicación del caso."));
        }

        if (caso.RechazadoEn is { } rechazadoEn)
        {
            hitos.Add(new(TiposHito.Rechazado, rechazadoEn, caso.MotivoRechazo switch
            {
                MotivoRechazo.Duplicado => "La radicación se rechazó: el evento ya estaba reportado en otro caso.",
                MotivoRechazo.NoAfiliado => "La radicación se rechazó: no se encontró la afiliación a la ARL.",
                _ => "La radicación se rechazó."
            }));
        }

        hitos.AddRange(caso.Remisiones.Select(r =>
            new HitoResponse(TiposHito.RemitidoAIps, r.RemitidoEn, "La ARL remitió el caso a una IPS para su atención.")));

        hitos.AddRange(c.Incapacidades.Select(i => new HitoResponse(
            TiposHito.IncapacidadRegistrada,
            i.RegistradaEn,
            $"Se registró una incapacidad de {i.Dias} día(s), del {i.FechaInicio:yyyy-MM-dd} al {i.FechaFin:yyyy-MM-dd}.")));

        foreach (DictamenNotificado dictamen in c.Dictamenes)
        {
            hitos.Add(new(TiposHito.DictamenNotificado, dictamen.NotificadoEn,
                "Se notificó el dictamen de calificación. Puedes solicitar la copia de tu historia clínica."));

            if (dictamen.Estado == EstadoNotificacionDictamen.EnFirme)
            {
                hitos.Add(new(TiposHito.DictamenEnFirme, dictamen.VenceControversiaEn,
                    "El dictamen quedó en firme: venció el término para controvertirlo."));
            }
        }

        return [.. hitos.OrderBy(h => h.Fecha)];
    }

    public static CasoPacienteResponse Respuesta(CasoDelPaciente c) => new(
        c.Caso.Id.Value,
        c.Caso.Radicado,
        c.Caso.TipoEvento.ToString(),
        c.Caso.Estado.ToString(),
        c.Caso.FechaEvento,
        c.Caso.TenantId.Value,
        LineaTiempo(c).Max(h => h.Fecha));
}

// ── Mis casos ─────────────────────────────────────────────────────────────────

/// <summary><c>GET /v1/paciente/mis-casos</c>: los casos de los documentos vinculados, de cualquier ARL.</summary>
public sealed record ListMisCasosQuery : IQuery<IReadOnlyList<CasoPacienteResponse>>;

internal sealed class ListMisCasosQueryHandler(ITenantContext tenantContext, PortalPaciente portal)
    : IQueryHandler<ListMisCasosQuery, IReadOnlyList<CasoPacienteResponse>>
{
    public async Task<Result<IReadOnlyList<CasoPacienteResponse>>> Handle(
        ListMisCasosQuery query, CancellationToken cancellationToken)
    {
        if (!tenantContext.EsPaciente())
        {
            return Result.Failure<IReadOnlyList<CasoPacienteResponse>>(PacienteErrors.ActorNoAutorizado);
        }

        List<CasoDelPaciente> casos = await portal.CasosAsync(casoId: null, cancellationToken);
        return casos.Select(PortalPaciente.Respuesta).ToList();
    }
}

// ── Resumen ───────────────────────────────────────────────────────────────────

/// <summary>
/// <c>GET /v1/paciente/resumen</c>: los casos activos, las incapacidades vigentes hoy (en Colombia) y los próximos hitos
/// (el fin de cada incapacidad vigente y el vencimiento de la controversia de un dictamen notificado).
/// </summary>
public sealed record GetResumenPacienteQuery : IQuery<ResumenPacienteResponse>;

internal sealed class GetResumenPacienteQueryHandler(ITenantContext tenantContext, PortalPaciente portal, TimeProvider time)
    : IQueryHandler<GetResumenPacienteQuery, ResumenPacienteResponse>
{
    public async Task<Result<ResumenPacienteResponse>> Handle(GetResumenPacienteQuery query, CancellationToken cancellationToken)
    {
        if (!tenantContext.EsPaciente())
        {
            return Result.Failure<ResumenPacienteResponse>(PacienteErrors.ActorNoAutorizado);
        }

        List<CasoDelPaciente> casos = await portal.CasosAsync(casoId: null, cancellationToken);
        DateTimeOffset ahora = time.GetUtcNow();
        DateOnly hoy = CalculadoraTerminos.FechaLocal(ahora);

        List<IncapacidadVigenteResponse> vigentes = [.. casos
            .SelectMany(c => c.Incapacidades)
            .Where(i => i.FechaInicio <= hoy && hoy <= i.FechaFin)
            .OrderBy(i => i.FechaFin)
            .Select(i => new IncapacidadVigenteResponse(i.CasoMedicoId.Value, i.FechaInicio, i.FechaFin, i.Dias))];

        IEnumerable<ProximoHitoResponse> finIncapacidad = vigentes
            .Select(i => new ProximoHitoResponse(i.CasoId, TiposHito.FinIncapacidad, i.FechaFin));

        IEnumerable<ProximoHitoResponse> controversias = casos
            .SelectMany(c => c.Dictamenes.Select(d => (c.Caso.Id, Dictamen: d)))
            .Where(x => x.Dictamen.Estado == EstadoNotificacionDictamen.Notificado && x.Dictamen.VenceControversiaEn >= ahora)
            .Select(x => new ProximoHitoResponse(
                x.Id.Value, TiposHito.VenceControversia, CalculadoraTerminos.FechaLocal(x.Dictamen.VenceControversiaEn)));

        return new ResumenPacienteResponse(
            [.. casos.Where(c => c.Activo).Select(PortalPaciente.Respuesta)],
            vigentes,
            [.. finIncapacidad.Concat(controversias).OrderBy(h => h.Fecha)]);
    }
}

// ── Línea de tiempo ───────────────────────────────────────────────────────────

/// <summary>
/// <c>GET /v1/paciente/casos/{id}/linea-tiempo</c>: los hitos administrativos del caso. Un caso no vinculado responde
/// <c>404</c>, como uno inexistente. Se audita como <c>LEER / CASO</c>.
/// </summary>
public sealed record GetLineaTiempoQuery(CasoMedicoId CasoId) : IQuery<IReadOnlyList<HitoResponse>>, IAuditedQuery
{
    public AccionAuditoria Accion => AccionAuditoria.Leer;

    public ObjetivoAuditoria Objetivo => new(RecursoAuditoria.Caso, CasoId.Value.ToString(), CasoId.Value);
}

internal sealed class GetLineaTiempoQueryHandler(ITenantContext tenantContext, PortalPaciente portal, IAuditScope auditScope)
    : IQueryHandler<GetLineaTiempoQuery, IReadOnlyList<HitoResponse>>
{
    public async Task<Result<IReadOnlyList<HitoResponse>>> Handle(GetLineaTiempoQuery query, CancellationToken cancellationToken)
    {
        if (!tenantContext.EsPaciente())
        {
            return Result.Failure<IReadOnlyList<HitoResponse>>(PacienteErrors.ActorNoAutorizado);
        }

        CasoDelPaciente? caso = (await portal.CasosAsync(query.CasoId, cancellationToken)).SingleOrDefault();
        if (caso is null)
        {
            return Result.Failure<IReadOnlyList<HitoResponse>>(CasoMedicoErrors.NoEncontrado);
        }

        auditScope.TenantDelRecurso(caso.Caso.TenantId);
        return PortalPaciente.LineaTiempo(caso);
    }
}

// ── Incapacidades ─────────────────────────────────────────────────────────────

/// <summary><c>GET /v1/paciente/incapacidades</c>: todas las de los casos vinculados, sin diagnóstico.</summary>
public sealed record ListIncapacidadesPacienteQuery : IQuery<IReadOnlyList<IncapacidadPacienteResponse>>;

internal sealed class ListIncapacidadesPacienteQueryHandler(ITenantContext tenantContext, IReadDbContext db)
    : IQueryHandler<ListIncapacidadesPacienteQuery, IReadOnlyList<IncapacidadPacienteResponse>>
{
    public async Task<Result<IReadOnlyList<IncapacidadPacienteResponse>>> Handle(
        ListIncapacidadesPacienteQuery query, CancellationToken cancellationToken)
    {
        if (!tenantContext.EsPaciente())
        {
            return Result.Failure<IReadOnlyList<IncapacidadPacienteResponse>>(PacienteErrors.ActorNoAutorizado);
        }

        // The query filter (and the RLS) leave only those of a linked case.
        List<Incapacidad> incapacidades = await db.Incapacidades
            .OrderByDescending(i => i.FechaInicio)
            .ToListAsync(cancellationToken);

        return incapacidades.Select(i => new IncapacidadPacienteResponse(
            i.Id.Value,
            i.CasoMedicoId.Value,
            CodigosIncapacidad.Codigo(i.Tipo),
            i.FechaInicio,
            i.FechaFin,
            i.Dias,
            i.DiasAcumulados,
            CodigosIncapacidad.Codigo(i.Estado))).ToList();
    }
}
