using System.Reflection;
using Halsa.Backend.Mediation.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Halsa.Backend.Mediation.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the mediator, the discovered handlers and the configured pipeline behaviours.
    /// </summary>
    public static IServiceCollection AddMediation(
        this IServiceCollection services,
        Action<MediationConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var configuration = new MediationConfiguration();
        configure(configuration);

        if (configuration.Assemblies.Count == 0)
        {
            throw new InvalidOperationException(
                "No assemblies were registered. Call RegisterServicesFromAssemblyContaining<T>() at least once.");
        }

        services.TryAddSingleton(
            typeof(INotificationPublisher),
            configuration.NotificationPublisherType);

        services.TryAddScoped<IMediator, Mediator>();
        services.TryAddScoped<ISender>(sp => sp.GetRequiredService<IMediator>());
        services.TryAddScoped<IPublisher>(sp => sp.GetRequiredService<IMediator>());

        RegisterHandlers(services, configuration);
        RegisterBehaviors(services, configuration);

        return services;
    }

    private static void RegisterHandlers(IServiceCollection services, MediationConfiguration configuration)
    {
        var requestHandlerOwners = new Dictionary<Type, Type>();

        foreach (Assembly assembly in configuration.Assemblies)
        {
            foreach (Type type in GetLoadableTypes(assembly))
            {
                if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
                {
                    continue;
                }

                foreach (Type contract in type.GetInterfaces().Where(i => i.IsGenericType))
                {
                    Type definition = contract.GetGenericTypeDefinition();

                    if (definition == typeof(IRequestHandler<,>))
                    {
                        Type requestType = contract.GetGenericArguments()[0];

                        if (requestHandlerOwners.TryGetValue(requestType, out Type? existing) && existing != type)
                        {
                            throw new DuplicateHandlerException(requestType, existing, type);
                        }

                        requestHandlerOwners[requestType] = type;
                        services.TryAdd(new ServiceDescriptor(contract, type, configuration.HandlerLifetime));
                    }
                    else if (definition == typeof(INotificationHandler<>))
                    {
                        services.TryAddEnumerable(
                            new ServiceDescriptor(contract, type, configuration.HandlerLifetime));
                    }
                }
            }
        }
    }

    private static void RegisterBehaviors(IServiceCollection services, MediationConfiguration configuration)
    {
        foreach (Type openBehavior in configuration.OpenBehaviors)
        {
            services.Add(new ServiceDescriptor(
                typeof(IPipelineBehavior<,>),
                openBehavior,
                configuration.HandlerLifetime));
        }

        foreach ((Type serviceType, Type implementationType) in configuration.ClosedBehaviors)
        {
            services.Add(new ServiceDescriptor(serviceType, implementationType, configuration.HandlerLifetime));
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(t => t is not null)!;
        }
    }
}
