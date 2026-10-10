using System.Reflection;
using Halsa.Backend.Api.Endpoints;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Halsa.Backend.Api.Extensions;

public static class EndpointExtensions
{
    public static IServiceCollection AddEndpoints(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);

        ServiceDescriptor[] descriptors = [.. assembly
            .DefinedTypes
            .Where(type => type is { IsAbstract: false, IsInterface: false } &&
                           type.IsAssignableTo(typeof(IEndpoint)))
            .Select(type => ServiceDescriptor.Transient(typeof(IEndpoint), type))];

        services.TryAddEnumerable(descriptors);

        return services;
    }

    public static IApplicationBuilder MapEndpoints(this WebApplication app, RouteGroupBuilder? group = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        IEndpointRouteBuilder builder = group ?? (IEndpointRouteBuilder)app;

        foreach (IEndpoint endpoint in app.Services.GetRequiredService<IEnumerable<IEndpoint>>())
        {
            endpoint.MapEndpoint(builder);
        }

        return app;
    }

    /// <summary>Requires a permission claim rather than a role: policies are created on demand.</summary>
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.RequireAuthorization(permission);
    }
}
