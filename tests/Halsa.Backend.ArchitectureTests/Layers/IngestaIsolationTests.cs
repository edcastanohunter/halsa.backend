using NetArchTest.Rules;

namespace Halsa.Backend.ArchitectureTests.Layers;

/// <summary>
/// SPEC 05: <c>halsa_ingesta</c> escribe en varios esquemas, la historia clínica incluida. Su contexto solo
/// puede usarlo la radicación: <c>Halsa.Backend.Application.Integraciones</c> (e Infrastructure, que lo
/// implementa y lo registra).
/// </summary>
public sealed class IngestaIsolationTests : BaseTest
{
    private const string IntegracionesNamespace = "Halsa.Backend.Application.Integraciones";
    private const string IngestaDbContextInterface = "Halsa.Backend.Application.Abstractions.Integraciones.IIngestaDbContext";

    [Fact]
    public void IngestaDbContext_ShouldOnlyBeUsedByTheIntegracionesSlice()
    {
        TestResult result = Types.InAssemblies([DomainAssembly, ApplicationAssembly, ApiAssembly])
            .That()
            .DoNotResideInNamespaceStartingWith(IntegracionesNamespace)
            .And()
            .DoNotHaveName("IIngestaDbContext")
            .ShouldNot()
            .HaveDependencyOn(IngestaDbContextInterface)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void TheRule_ShouldSeeTheIntegracionesSliceUsingIt()
    {
        // Guard the guard: if the slice stopped depending on the interface, the rule above would pass vacuously.
        TestResult result = Types.InAssembly(ApplicationAssembly)
            .That()
            .ResideInNamespaceStartingWith(IntegracionesNamespace)
            .And()
            .HaveNameEndingWith("CommandHandler")
            .Should()
            .HaveDependencyOn(IngestaDbContextInterface)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue();
        Types.InAssembly(ApplicationAssembly)
            .That()
            .HaveDependencyOn(IngestaDbContextInterface)
            .GetTypes()
            .ShouldNotBeEmpty();
    }
}
