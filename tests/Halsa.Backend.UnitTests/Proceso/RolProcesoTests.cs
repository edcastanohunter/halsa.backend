using Halsa.Backend.Infrastructure;
using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.Infrastructure.Proceso;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Halsa.Backend.UnitTests.Proceso;

/// <summary>SPEC 09, paso 1: <c>Halsa:Rol</c> decide qué registra la misma imagen.</summary>
public sealed class RolProcesoTests
{
    [Theory]
    [InlineData("Api", RolProceso.Api)]
    [InlineData("worker", RolProceso.Worker)]
    [InlineData("AMBOS", RolProceso.Ambos)]
    public void ConfiguredRole_ShouldBeRead_InAnyEnvironment(string valor, RolProceso esperado) =>
        RolesProceso.Resolver(Configuracion(valor), "Production").ShouldBe(esperado);

    [Fact]
    public void MissingRole_InDevelopment_ShouldBeAmbos() =>
        RolesProceso.Resolver(Configuracion(null), "Development").ShouldBe(RolProceso.Ambos);

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void MissingRole_OutsideDevelopment_ShouldFail(string entorno) =>
        Should.Throw<InvalidOperationException>(() => RolesProceso.Resolver(Configuracion(null), entorno))
            .Message.ShouldContain("Halsa:Rol");

    [Theory]
    [InlineData("Migrador")]
    [InlineData("2")]
    public void UnknownRole_ShouldFail(string valor) =>
        Should.Throw<InvalidOperationException>(() => RolesProceso.Resolver(Configuracion(valor), "Development"));

    [Fact]
    public void Api_ShouldNotRegisterTheOutboxNorQuartz() =>
        // The framework's own (health-check publisher, data protection) are not background work.
        HostedServices(RolProceso.Api)
            .ShouldNotContain(t => t != null && (t.Namespace!.StartsWith("Halsa", StringComparison.Ordinal)
                                                 || t.Namespace.StartsWith("Quartz", StringComparison.Ordinal)));

    [Fact]
    public void Worker_ShouldRegisterQuartz() =>
        HostedServices(RolProceso.Worker).ShouldContain(t => t != null && t.Namespace!.StartsWith("Quartz", StringComparison.Ordinal));

    [Theory]
    [InlineData(RolProceso.Worker)]
    [InlineData(RolProceso.Ambos)]
    public void WorkerAndAmbos_ShouldRegisterTheOutboxProcessor(RolProceso rol) =>
        HostedServices(rol).ShouldContain(typeof(OutboxProcessor));

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IConfiguration Configuracion(string? rol) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [RolesProceso.Clave] = rol })
        .Build();

    /// <summary>The hosted services <c>AddInfrastructure</c> registers for <paramref name="rol"/>, without building them.</summary>
    private static List<Type?> HostedServices(RolProceso rol)
    {
        const string cadena = "Host=localhost;Database=halsa.backend;Username=nadie;Password=nada";

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = cadena,
                ["ConnectionStrings:Clinical"] = cadena,
                ["ConnectionStrings:Ingesta"] = cadena,
                ["ConnectionStrings:Worker"] = cadena
            })
            .Build();

        var services = new ServiceCollection();
        services.AddInfrastructure(configuration, rol);

        return [.. services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)];
    }
}
