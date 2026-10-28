using Halsa.Backend.Application.Abstractions.Integraciones;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Infrastructure.Integraciones;

/// <summary>
/// El gateway de afiliación fuera de Development y Testing mientras no exista el cliente del core de ninguna ARL
/// (SPEC 05): no aprueba ni rechaza a nadie. La radicación se recibe y se deduplica, y el caso queda <c>Radicado</c>
/// hasta que se integre el core y se vuelva a validar.
/// </summary>
internal sealed class AfiliacionNoIntegradaGateway : IAfiliacionGateway
{
    public Task<ResultadoAfiliacion> VerificarAsync(
        TenantId tenant, Identificacion trabajador, Identificacion empleador, DateTimeOffset fechaEvento, CancellationToken ct) =>
        Task.FromResult(ResultadoAfiliacion.SinVerificar);
}
