using NetArchTest.Rules;

namespace Halsa.Backend.ArchitectureTests.Layers;

/// <summary>
/// SPEC 07: the incapacidad upload writes through the ingestion's context (<c>halsa_ingesta</c>), which inserts into
/// the clinical history. Its port is only for <c>Halsa.Backend.Application.Incapacidades</c>, as
/// <c>IIngestaDbContext</c> is only for the radication (<see cref="IngestaIsolationTests"/>).
/// </summary>
public sealed class IncapacidadesIsolationTests : BaseTest
{
    private const string IncapacidadesNamespace = "Halsa.Backend.Application.Incapacidades";
    private const string IncapacidadesDbContextInterface =
        "Halsa.Backend.Application.Abstractions.Incapacidades.IIncapacidadesDbContext";

    [Fact]
    public void IncapacidadesDbContext_ShouldOnlyBeUsedByTheIncapacidadesSlice()
    {
        TestResult result = Types.InAssemblies([DomainAssembly, ApplicationAssembly, ApiAssembly])
            .That()
            .DoNotResideInNamespaceStartingWith(IncapacidadesNamespace)
            .And()
            .DoNotHaveName("IIncapacidadesDbContext")
            .ShouldNot()
            .HaveDependencyOn(IncapacidadesDbContextInterface)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void TheRule_ShouldSeeTheIncapacidadesSliceUsingIt()
    {
        // Guard the guard: if the slice stopped depending on the interface, the rule above would pass vacuously.
        Types.InAssembly(ApplicationAssembly)
            .That()
            .ResideInNamespaceStartingWith(IncapacidadesNamespace)
            .And()
            .HaveDependencyOn(IncapacidadesDbContextInterface)
            .GetTypes()
            .ShouldNotBeEmpty();
    }
}
