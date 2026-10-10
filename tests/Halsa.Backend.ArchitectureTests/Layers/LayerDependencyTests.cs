using NetArchTest.Rules;

namespace Halsa.Backend.ArchitectureTests.Layers;

public sealed class LayerDependencyTests : BaseTest
{
    [Fact]
    public void SharedKernel_ShouldNotDependOnAnyOtherLayer()
    {
        TestResult result = Types.InAssembly(SharedKernelAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(DomainNamespace, ApplicationNamespace, InfrastructureNamespace, ApiNamespace)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Domain_ShouldNotDependOnOuterLayers()
    {
        TestResult result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(ApplicationNamespace, InfrastructureNamespace, ApiNamespace)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Domain_ShouldNotDependOnPersistenceOrWebFrameworks()
    {
        TestResult result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Npgsql",
                "Halsa.Backend.Mediation")
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Application_ShouldNotDependOnInfrastructureOrApi()
    {
        TestResult result = Types.InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(InfrastructureNamespace, ApiNamespace)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Application_ShouldNotDependOnTheDatabaseProvider()
    {
        TestResult result = Types.InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("Npgsql", "Microsoft.AspNetCore")
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Infrastructure_ShouldNotDependOnApi()
    {
        TestResult result = Types.InAssembly(InfrastructureAssembly)
            .ShouldNot()
            .HaveDependencyOn(ApiNamespace)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Mediation_ShouldStayApplicationAgnostic()
    {
        TestResult result = Types.InAssembly(MediationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                DomainNamespace,
                ApplicationNamespace,
                InfrastructureNamespace,
                ApiNamespace,
                "Halsa.Backend.SharedKernel")
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }
}
