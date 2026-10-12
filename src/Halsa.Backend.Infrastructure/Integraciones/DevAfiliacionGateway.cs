using Halsa.Backend.Application.Abstractions.Integraciones;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.Infrastructure.Integraciones;

/// <summary>
/// El stub de afiliación de Development y Testing (SPEC 05): aprueba a todos salvo a los trabajadores cuyo número
/// termina en <c>000</c>, para poder probar el rechazo. El cliente real del core de cada ARL va en su propia spec.
/// </summary>
internal sealed class DevAfiliacionGateway : IAfiliacionGateway
{
    public const string SufijoNoAfiliado = "000";

    public Task<ResultadoAfiliacion> VerificarAsync(
        TenantId tenant, Identificacion trabajador, Identificacion empleador, DateTimeOffset fechaEvento, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(trabajador);

        return Task.FromResult(trabajador.Numero.EndsWith(SufijoNoAfiliado, StringComparison.Ordinal)
            ? ResultadoAfiliacion.NoAfiliado
            : ResultadoAfiliacion.Afiliado);
    }

    /// <summary>
    /// Development, Testing o sin host (los tests de integración). En cualquier otro entorno aprobaría casos
    /// reales sin consultar a nadie.
    /// </summary>
    public static bool Admitido(string? environmentName) =>
        environmentName is null
        || string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
        || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Se ejecuta al arrancar (<c>ValidateOnStart</c>): fuera de Development y Testing todavía no hay un gateway de
/// afiliación real, así que la API no arranca en vez de aprobar casos con el stub.
/// </summary>
internal sealed class AfiliacionGatewayValidator(string? environmentName) : IValidateOptions<IntegracionesOptions>
{
    public ValidateOptionsResult Validate(string? name, IntegracionesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.VentanaDuplicado <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("Integraciones:VentanaDuplicado debe ser positiva.");
        }

        return DevAfiliacionGateway.Admitido(environmentName)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"No hay un IAfiliacionGateway real para '{environmentName}': DevAfiliacionGateway solo se admite en Development y Testing.");
    }
}
