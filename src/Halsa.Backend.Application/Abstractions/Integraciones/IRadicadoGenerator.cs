using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Abstractions.Integraciones;

/// <summary>
/// El radicado <c>ATEL-{tenant}-{año}-{seq6}</c>, con una secuencia por tenant y año (<c>atel.secuencias_radicado</c>,
/// con RLS: no revela el volumen de las otras ARL). Toma el número dentro de la transacción abierta de la ingesta,
/// así que si la radicación se revierte el número vuelve a quedar libre.
/// </summary>
public interface IRadicadoGenerator
{
    /// <param name="ahora">El año es el de <paramref name="ahora"/> en Colombia (-05:00).</param>
    Task<string> SiguienteAsync(TenantId tenant, DateTimeOffset ahora, CancellationToken ct);
}
