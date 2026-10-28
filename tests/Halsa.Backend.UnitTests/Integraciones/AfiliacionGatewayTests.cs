using Halsa.Backend.Application.Abstractions.Integraciones;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;
using Halsa.Backend.Infrastructure.Integraciones;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.UnitTests.Integraciones;

/// <summary>
/// SPEC 05: el stub de afiliación nunca aprueba casos reales. Fuera de Development y Testing, sin core integrado,
/// ningún caso se aprueba ni se rechaza por afiliación.
/// </summary>
public sealed class AfiliacionGatewayTests
{
    private static readonly Identificacion Empleador = new("NIT", "900123456");

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Testing", true)]
    [InlineData(null, true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    public void TheStub_ShouldOnlyBeAdmittedInDevelopmentAndTesting(string? entorno, bool admitido) =>
        DevAfiliacionGateway.Admitido(entorno).ShouldBe(admitido);

    [Fact]
    public void PositiveWindow_ShouldPass() =>
        new IntegracionesOptionsValidator().Validate(null, new IntegracionesOptions()).Succeeded.ShouldBeTrue();

    [Fact]
    public void NonPositiveWindow_ShouldFail() =>
        new IntegracionesOptionsValidator()
            .Validate(null, new IntegracionesOptions { VentanaDuplicado = TimeSpan.Zero })
            .Failed.ShouldBeTrue();

    [Theory]
    [InlineData("1020304000", false)]
    [InlineData("1020304050", true)]
    public async Task DevGateway_ShouldRejectOnlyNumbersEndingIn000(string numero, bool afiliado)
    {
        ResultadoAfiliacion resultado = await new DevAfiliacionGateway().VerificarAsync(
            new TenantId("ARL_A"), new Identificacion("CC", numero), Empleador, DateTimeOffset.UtcNow, CancellationToken.None);

        (resultado == ResultadoAfiliacion.Afiliado).ShouldBe(afiliado);
    }

    [Theory]
    [InlineData("1020304000")]
    [InlineData("1020304050")]
    public async Task NotIntegratedGateway_ShouldNeverDecide(string numero) =>
        (await new AfiliacionNoIntegradaGateway().VerificarAsync(
            new TenantId("ARL_A"), new Identificacion("CC", numero), Empleador, DateTimeOffset.UtcNow, CancellationToken.None))
        .ShouldBe(ResultadoAfiliacion.SinVerificar);
}
