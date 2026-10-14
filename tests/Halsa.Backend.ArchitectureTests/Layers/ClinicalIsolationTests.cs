using Halsa.Backend.Application.Abstractions.Messaging;
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
    private const string ClinicoQueriesNamespace = "Halsa.Backend.Application.Clinico.Queries";
    private const string ClinicoDomainNamespace = "Halsa.Backend.Domain.Clinico";
    private const string ClinicalDbContextInterface = "Halsa.Backend.Application.Abstractions.Clinico.IClinicalDbContext";
    private const string ClinicalAuditTrailInterface = "Halsa.Backend.Application.Abstractions.Auditoria.IClinicalAuditTrail";
    private const string ClinicalAuthorizationInterface = "Halsa.Backend.Infrastructure.Authorization.IClinicalAuthorization";

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
    public void Api_ShouldOnlyExposeTheClinicalReadSide_BehindTheClinicalAbac()
    {
        // SPEC 08 opens the clinical reads, but only with the per-case authorization (assignment): an endpoint that
        // sends a clinical query without IClinicalAuthorization would let any holder of the permission read any HC.
        TestResult result = Types.InAssembly(ApiAssembly)
            .That()
            .HaveDependencyOn(ClinicoQueriesNamespace)
            .Should()
            .HaveDependencyOn(ClinicalAuthorizationInterface)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
        Types.InAssembly(ApiAssembly).That().HaveDependencyOn(ClinicoQueriesNamespace).GetTypes().ShouldNotBeEmpty();
    }

    [Fact]
    public void ClinicalCommandHandlers_ShouldDependOnTheClinicalAuditTrail()
    {
        // SPEC 04: a clinical write without its audit row in the same transaction is a compliance breach.
        string[] handlers = [.. ApplicationAssembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith(ClinicoApplicationNamespace, StringComparison.Ordinal) == true &&
                           type is { IsClass: true, IsAbstract: false } &&
                           type.GetInterfaces().Any(IsCommandHandler))
            .Select(type => type.Name)];

        handlers.ShouldNotBeEmpty();

        TestResult result = Types.InAssembly(ApplicationAssembly)
            .That()
            .ResideInNamespaceStartingWith(ClinicoApplicationNamespace)
            .And()
            .HaveNameMatching($"^({string.Join('|', handlers)})$")
            .Should()
            .HaveDependencyOn(ClinicalAuditTrailInterface)
            .GetResult();

        result.FailingTypeNames.ShouldBeNull();
    }

    private static bool IsCommandHandler(Type contract) =>
        contract.IsGenericType &&
        (contract.GetGenericTypeDefinition() == typeof(ICommandHandler<>) ||
         contract.GetGenericTypeDefinition() == typeof(ICommandHandler<,>));

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
