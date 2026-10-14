using NetArchTest.Rules;

namespace Halsa.Backend.ArchitectureTests.Layers;

/// <summary>
/// SPEC 08: the PCL dictamen writes through the ingestion's context (<c>halsa_ingesta</c>), which inserts into the
/// clinical history and updates the case's calificación. Its port is only for <c>Halsa.Backend.Application.Dictamenes</c>,
/// as <see cref="IncapacidadesIsolationTests"/> does for the incapacidades.
/// </summary>
public sealed class DictamenesIsolationTests : BaseTest
{
    private const string DictamenesNamespace = "Halsa.Backend.Application.Dictamenes";
    private const string DictamenesDbContextInterface =
        "Halsa.Backend.Application.Abstractions.Dictamenes.IDictamenesDbContext";

    [Fact]
    public void DictamenesDbContext_ShouldOnlyBeUsedByTheDictamenesSlice()
    {
        TestResult result = Types.InAssemblies([DomainAssembly, ApplicationAssembly, ApiAssembly])
            .That()
            .DoNotResideInNamespaceStartingWith(DictamenesNamespace)
            .And()
            .DoNotHaveName("IDictamenesDbContext")
            .ShouldNot()
            .HaveDependencyOn(DictamenesDbContextInterface)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void TheRule_ShouldSeeTheDictamenesSliceUsingIt()
    {
        // Guard the guard: if the slice stopped depending on the interface, the rule above would pass vacuously.
        Types.InAssembly(ApplicationAssembly)
            .That()
            .ResideInNamespaceStartingWith(DictamenesNamespace)
            .And()
            .HaveDependencyOn(DictamenesDbContextInterface)
            .GetTypes()
            .ShouldNotBeEmpty();
    }
}
