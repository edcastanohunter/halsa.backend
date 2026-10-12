using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Integraciones.Canonico;
using Halsa.Backend.Application.Integraciones.Commands.Radicar;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Integraciones.Queries.ConsultarRadicacion;

/// <summary>El estado del procesamiento asíncrono de una radicación, para el core de la ARL que la envió.</summary>
public sealed record ConsultarRadicacionQuery(TipoReporte Tipo, Guid SolicitudId) : IQuery<RadicacionResponse>;

/// <summary>
/// Lee con <c>halsa_app</c>: la solicitud y su caso pasan por los query filters y la RLS, así que una solicitud
/// de otro tenant responde igual que una que no existe.
/// </summary>
internal sealed class ConsultarRadicacionQueryHandler(IReadDbContext db, ITenantContext tenantContext)
    : IQueryHandler<ConsultarRadicacionQuery, RadicacionResponse>
{
    /// <summary>El <c>motivoRechazo</c> de un caso rechazado por afiliación.</summary>
    internal static readonly Problema NoAfiliado = new(
        "https://errors.halsa.co/no-afiliado",
        "Trabajador no afiliado",
        422,
        "Integracion.NoAfiliado",
        "El core de la ARL no reporta afiliación del trabajador con el empleador en la fecha del evento.");

    public async Task<Result<RadicacionResponse>> Handle(ConsultarRadicacionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!RadicarReporteCommandHandler.PuedeRadicar(tenantContext))
        {
            return Result.Failure<RadicacionResponse>(IntegracionErrors.ActorNoAutorizado);
        }

        var fila = await db.Solicitudes
            .Where(s => s.Id == query.SolicitudId && s.Tipo == query.Tipo)
            .Join(db.CasosMedicos.AsNoTracking(), s => s.CasoMedicoId, c => c.Id, (s, c) => new
            {
                s.Id,
                s.CasoMedicoId,
                s.Radicado,
                s.RecibidoEn,
                c.Estado,
                c.MotivoRechazo
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (fila is null)
        {
            return Result.Failure<RadicacionResponse>(IntegracionErrors.SolicitudNoEncontrada);
        }

        (string estado, Problema? motivo) = Estado(fila.Estado, fila.MotivoRechazo);

        return Result.Success(new RadicacionResponse(
            fila.Id, fila.CasoMedicoId.Value, fila.Radicado, estado, fila.RecibidoEn, [], motivo));
    }

    /// <summary>Cualquier estado posterior a la validación cuenta como <c>VALIDADO</c>.</summary>
    internal static (string Estado, Problema? Motivo) Estado(EstadoCaso estado, MotivoRechazo? motivo) => estado switch
    {
        EstadoCaso.Radicado => (EstadosRadicacion.Radicado, null),
        EstadoCaso.Rechazado when motivo == MotivoRechazo.Duplicado => (EstadosRadicacion.Duplicado, null),
        EstadoCaso.Rechazado => (EstadosRadicacion.Rechazado, NoAfiliado),
        _ => (EstadosRadicacion.Validado, null)
    };
}
