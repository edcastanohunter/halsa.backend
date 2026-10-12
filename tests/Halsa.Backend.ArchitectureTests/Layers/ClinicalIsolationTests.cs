using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using NetArchTest.Rules;

namespace Halsa.Backend.ArchitectureTests.Layers;

/// <summary>
/// SPEC 03, barriers 1 and 2: administrative code cannot reach the clinical detail, not even by
/// accident. Only <c>Halsa.Backend.Application.Clinico</c> (and Infrastructure, which implements it)
/// may touch <c>IClinicalDbContext</c>, and the administrative model never maps the clinico schema.
/// </summary>
public sealed class ClinicalIsolationTests : BaseTest
{
    private const string CasosNamespace = "Halsa.Backend.Application.Casos";
    private const string ClinicoApplicationNamespace = "Halsa.Backend.Application.Clinico";
    private const string ClinicoDomainNamespace = "Halsa.Backend.Domain.Clinico";
    private const string ClinicalDbContextInterface = "Halsa.Backend.Application.Abstractions.Clinico.IClinicalDbContext";

    [Fact]
    public void Casos_ShouldNotDependOnTheClinicalDomainOrContext()
    {
        TestResult result = Types.InAssembly(ApplicationAssembly)
            .That()
            .ResideInNamespace(CasosNamespace)
            .ShouldNot()
            .HaveDependencyOnAny(ClinicoDomainNamespace, ClinicalDbContextInterface)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void ClinicalDbContext_ShouldOnlyBeUsedByTheClinicalApplicationSlice()
    {
        // Infrastructure implements and registers it, so only the inner layers and the API are checked.
        TestResult result = Types.InAssemblies([DomainAssembly, ApplicationAssembly, ApiAssembly])
            .That()
            .DoNotResideInNamespace(ClinicoApplicationNamespace)
            .And()
            .DoNotHaveName("IClinicalDbContext")
            .ShouldNot()
            .HaveDependencyOn(ClinicalDbContextInterface)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void Api_ShouldNotExposeTheClinicalSlice()
    {
        // No clinical endpoint until audit (SPEC 04) and per-resource authorization (SPEC 08) exist.
        TestResult result = Types.InAssembly(ApiAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(ClinicoApplicationNamespace, ClinicoDomainNamespace)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    [Fact]
    public void ApplicationDbContext_ShouldNotMapAnyEntityToTheClinicoSchema()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseSnakeCaseNamingConvention()
            .Options;

        using var context = new ApplicationDbContext(options, NullTenantContext.Instance);

        string[] clinicos = [.. context.Model.GetEntityTypes()
            .Where(entity => entity.GetSchema() == Schemas.Clinico)
            .Select(entity => entity.Name)];

        clinicos.ShouldBeEmpty();

        // Guard the guard: the model really is built from this assembly's configurations.
        context.Model.GetEntityTypes().ShouldNotBeEmpty();
    }
}
