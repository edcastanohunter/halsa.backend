using System.Reflection;

namespace Halsa.Backend.ArchitectureTests;

/// <summary>
/// Architecture rules are executable documentation: they fail the build the day someone reaches
/// across a layer, which is far cheaper than finding out at review time.
/// </summary>
public abstract class BaseTest
{
    protected static readonly Assembly SharedKernelAssembly = typeof(SharedKernel.Results.Result).Assembly;
    protected static readonly Assembly MediationAssembly = typeof(Mediation.ISender).Assembly;
    protected static readonly Assembly DomainAssembly = typeof(Domain.DomainAssemblyMarker).Assembly;
    protected static readonly Assembly ApplicationAssembly = typeof(Application.DependencyInjection).Assembly;
    protected static readonly Assembly InfrastructureAssembly = typeof(Infrastructure.DependencyInjection).Assembly;
    protected static readonly Assembly ApiAssembly = typeof(Api.Endpoints.IEndpoint).Assembly;

    protected const string DomainNamespace = "Halsa.Backend.Domain";
    protected const string ApplicationNamespace = "Halsa.Backend.Application";
    protected const string InfrastructureNamespace = "Halsa.Backend.Infrastructure";
    protected const string ApiNamespace = "Halsa.Backend.Api";
}
