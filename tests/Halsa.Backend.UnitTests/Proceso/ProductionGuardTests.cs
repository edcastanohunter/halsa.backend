using Halsa.Backend.Application.Abstractions.Integraciones;
using Halsa.Backend.Infrastructure.Integraciones;
using Halsa.Backend.Infrastructure.Proceso;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Halsa.Backend.UnitTests.Proceso;

/// <summary>SPEC 13, paso 1: cada condición de <see cref="ProductionGuard"/> detiene el arranque y se nombra.</summary>
public sealed class ProductionGuardTests
{
    [Fact]
    public void ProductionConfiguration_ShouldPass() =>
        ProductionGuard.Verificar(Configuracion()).ShouldBeEmpty();

    [Theory]
    [InlineData("Keycloak:RequireHttpsMetadata", "false", "Keycloak:RequireHttpsMetadata")]
    [InlineData("Keycloak:PersonalAuthority", "http://auth.halsa.co/realms/halsa", "Keycloak:PersonalAuthority")]
    [InlineData("Keycloak:PacientesAuthority", "http://auth.halsa.co/realms/halsa-pacientes", "Keycloak:PacientesAuthority")]
    [InlineData("Keycloak:PersonalAuthority", "", "Keycloak:PersonalAuthority")]
    [InlineData("Halsa:Rol", "", "Halsa:Rol")]
    [InlineData("Halsa:Rol", "Ambos", "Halsa:Rol")]
    [InlineData("Halsa:Rol", "ambos", "Halsa:Rol")]
    [InlineData("Integraciones:ExigirCertificado", "false", "Integraciones:ExigirCertificado")]
    [InlineData("Integraciones:ExigirCertificado", null, "Integraciones:ExigirCertificado")]
    [InlineData("Webhooks:PermitirDestinosLocales", "true", "Webhooks:PermitirDestinosLocales")]
    [InlineData("Clinico:KekBase64", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "Clinico:KekBase64")]
    [InlineData("ConnectionStrings:Database", "Host=postgres;Database=halsa;Username=halsa_owner;Password=x", "halsa_owner")]
    [InlineData("ConnectionStrings:Database", "Host=postgres;Database=halsa;Username=postgres;Password=x", "postgres")]
    public void EachCondition_ShouldFail_AndNameIt(string clave, string? valor, string nombrada)
    {
        IReadOnlyList<string> fallas = ProductionGuard.Verificar(Configuracion((clave, valor)));

        fallas.Count.ShouldBe(1);
        fallas[0].ShouldContain(nombrada);
    }

    [Theory]
    [InlineData("Api")]
    [InlineData("Worker")]
    public void ApiAndWorker_ShouldBeAccepted(string rol) =>
        ProductionGuard.Verificar(Configuracion(("Halsa:Rol", rol))).ShouldBeEmpty();

    [Fact]
    public void SeveralConditions_ShouldAllBeReported() =>
        ProductionGuard.Verificar(Configuracion(
                ("Keycloak:RequireHttpsMetadata", "false"),
                ("Webhooks:PermitirDestinosLocales", "true")))
            .Count.ShouldBe(2);

    [Fact]
    public void TheAffiliationStub_ShouldFail()
    {
        var services = new ServiceCollection();
        services.AddScoped<IAfiliacionGateway, DevAfiliacionGateway>();

        ProductionGuard.VerificarServicios(services.BuildServiceProvider())
            .ShouldHaveSingleItem().ShouldContain(nameof(DevAfiliacionGateway));
    }

    [Fact]
    public void AGatewayThatCannotBeBuilt_ShouldBeLeftToItsOwnValidator()
    {
        var services = new ServiceCollection();
        services.AddScoped<IAfiliacionGateway>(_ => throw new InvalidOperationException("sin gateway real"));

        ProductionGuard.VerificarServicios(services.BuildServiceProvider()).ShouldBeEmpty();
    }

    [Fact]
    public void Abortar_ShouldWriteEachConditionAndReturn1()
    {
        using var salida = new StringWriter();

        ProductionGuard.Abortar(["uno", "dos"], salida).ShouldBe(1);

        salida.ToString().ShouldContain("uno");
        salida.ToString().ShouldContain("dos");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A valid production configuration, with <paramref name="cambios"/> on top (a null value removes the key).</summary>
    private static IConfiguration Configuracion(params (string Clave, string? Valor)[] cambios)
    {
        var valores = new Dictionary<string, string?>
        {
            ["Keycloak:RequireHttpsMetadata"] = "true",
            ["Keycloak:PersonalAuthority"] = "https://auth.halsa.co/realms/halsa",
            ["Keycloak:PacientesAuthority"] = "https://auth.halsa.co/realms/halsa-pacientes",
            ["Halsa:Rol"] = "Api",
            ["Integraciones:ExigirCertificado"] = "true",
            ["Webhooks:PermitirDestinosLocales"] = "false",
            ["Clinico:KekPath"] = "/run/secrets/halsa_kek",
            ["ConnectionStrings:Database"] = "Host=postgres;Database=halsa;Username=halsa_app;Password=x"
        };

        foreach ((string clave, string? valor) in cambios)
        {
            if (valor is null)
            {
                valores.Remove(clave);
            }
            else
            {
                valores[clave] = valor;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
    }
}
