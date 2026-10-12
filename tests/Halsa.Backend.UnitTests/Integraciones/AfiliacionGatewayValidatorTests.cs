using Halsa.Backend.Application.Abstractions.Integraciones;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;
using Halsa.Backend.Infrastructure.Integraciones;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.UnitTests.Integraciones;

/// <summary>SPEC 05: el stub de afiliación nunca aprueba casos reales; fuera de Development y Testing la API no arranca.</summary>
public sealed class AfiliacionGatewayValidatorTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData(null)]
    public void DevAndTesting_ShouldStart(string? entorno) =>
        new AfiliacionGatewayValidator(entorno).Validate(null, new IntegracionesOptions())
            .Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void AnyOtherEnvironment_ShouldFailAtStartup(string entorno)
    {
        ValidateOptionsResult result = new AfiliacionGatewayValidator(entorno).Validate(null, new IntegracionesOptions());

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("IAfiliacionGateway");
    }

    [Fact]
    public void NonPositiveWindow_ShouldFail() =>
        new AfiliacionGatewayValidator("Development")
            .Validate(null, new IntegracionesOptions { VentanaDuplicado = TimeSpan.Zero })
            .Failed.ShouldBeTrue();

    [Theory]
    [InlineData("1020304000", false)]
    [InlineData("1020304050", true)]
    public async Task DevGateway_ShouldRejectOnlyNumbersEndingIn000(string numero, bool afiliado)
    {
        ResultadoAfiliacion resultado = await new DevAfiliacionGateway().VerificarAsync(
            new TenantId("ARL_A"), new Identificacion("CC", numero),
            new Identificacion("NIT", "900123456"), DateTimeOffset.UtcNow, CancellationToken.None);

        (resultado == ResultadoAfiliacion.Afiliado).ShouldBe(afiliado);
    }
}
