using NetArchTest.Rules;

namespace Halsa.Backend.ArchitectureTests.Layers;

public sealed class ConventionTests : BaseTest
{
    [Fact]
    public void Handlers_ShouldBeSealed()
    {
        TestResult result = Types.InAssembly(ApplicationAssembly)
            .That()
            .HaveNameEndingWith("Handler")
            .Should()
            .BeSealed()
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Handlers_ShouldNotBePublic()
    {
        // Handlers are an implementation detail: callers send a command, they never new one up.
        TestResult result = Types.InAssembly(ApplicationAssembly)
            .That()
            .HaveNameEndingWith("Handler")
            .Should()
            .NotBePublic()
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Validators_ShouldBeSealedAndInternal()
    {
        TestResult result = Types.InAssembly(ApplicationAssembly)
            .That()
            .HaveNameEndingWith("Validator")
            .Should()
            .BeSealed()
            .And()
            .NotBePublic()
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Endpoints_ShouldBeSealed()
    {
        TestResult result = Types.InAssembly(ApiAssembly)
            .That()
            .ImplementInterface(typeof(Api.Endpoints.IEndpoint))
            .Should()
            .BeSealed()
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void DomainEvents_ShouldBeSealed()
    {
        TestResult result = Types.InAssembly(DomainAssembly)
            .That()
            .ImplementInterface(typeof(SharedKernel.Primitives.IDomainEvent))
            .Should()
            .BeSealed()
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }
}
