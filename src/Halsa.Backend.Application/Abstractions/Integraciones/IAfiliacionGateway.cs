using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Abstractions.Integraciones;

/// <summary>
/// La consulta de afiliación al core de la ARL (SOAP o REST, una implementación por ARL integrada, en su propia
/// spec). Si el core no responde, lanza: el mensaje del outbox se reintenta y el caso sigue radicado.
/// </summary>
public interface IAfiliacionGateway
{
    Task<ResultadoAfiliacion> VerificarAsync(
        TenantId tenant,
        Identificacion trabajador,
        Identificacion empleador,
        DateTimeOffset fechaEvento,
        CancellationToken ct);
}

public enum ResultadoAfiliacion
{
    Afiliado = 1,
    NoAfiliado = 2,

    /// <summary>
    /// No hay core integrado para consultar (<c>AfiliacionNoIntegradaGateway</c>): el caso queda <c>Radicado</c>, sin
    /// validar ni rechazar, hasta que exista el cliente real y se vuelva a validar.
    /// </summary>
    SinVerificar = 3
}
