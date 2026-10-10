using System.Reflection;
using Halsa.Backend.Mediation.Publishers;
using Microsoft.Extensions.DependencyInjection;

namespace Halsa.Backend.Mediation.DependencyInjection;

/// <summary>Fluent configuration for <see cref="ServiceCollectionExtensions.AddMediation"/>.</summary>
public sealed class MediationConfiguration
{
    internal List<Assembly> Assemblies { get; } = [];

    internal List<Type> OpenBehaviors { get; } = [];

    internal List<(Type ServiceType, Type ImplementationType)> ClosedBehaviors { get; } = [];

    /// <summary>Lifetime used for discovered handlers. Scoped keeps them aligned with the DbContext.</summary>
    public ServiceLifetime HandlerLifetime { get; set; } = ServiceLifetime.Scoped;

    /// <summary>Strategy used to fan notifications out. Defaults to sequential.</summary>
    public Type NotificationPublisherType { get; set; } = typeof(SequentialNotificationPublisher);

    public MediationConfiguration RegisterServicesFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        if (!Assemblies.Contains(assembly))
        {
            Assemblies.Add(assembly);
        }

        return this;
    }

    public MediationConfiguration RegisterServicesFromAssemblyContaining<TMarker>() =>
        RegisterServicesFromAssembly(typeof(TMarker).Assembly);

    public MediationConfiguration RegisterServicesFromAssemblyContaining(Type marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        return RegisterServicesFromAssembly(marker.Assembly);
    }

    /// <summary>
    /// Adds an open generic behaviour, e.g. <c>typeof(LoggingBehavior&lt;,&gt;)</c>.
    /// Registration order is pipeline order: the first one added is the outermost.
    /// </summary>
    public MediationConfiguration AddOpenBehavior(Type openBehaviorType)
    {
        ArgumentNullException.ThrowIfNull(openBehaviorType);

        if (!openBehaviorType.IsGenericTypeDefinition)
        {
            throw new ArgumentException(
                $"'{openBehaviorType.FullName}' is not an open generic type. Use AddBehavior for closed behaviours.",
                nameof(openBehaviorType));
        }

        OpenBehaviors.Add(openBehaviorType);
        return this;
    }

    /// <summary>Adds a behaviour that is closed over specific request and response types.</summary>
    public MediationConfiguration AddBehavior<TBehavior>()
        where TBehavior : class => AddBehavior(typeof(TBehavior));

    public MediationConfiguration AddBehavior(Type behaviorType)
    {
        ArgumentNullException.ThrowIfNull(behaviorType);

        Type[] implemented = [.. behaviorType
            .GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>))];

        if (implemented.Length == 0)
        {
            throw new ArgumentException(
                $"'{behaviorType.FullName}' does not implement IPipelineBehavior<TRequest, TResponse>.",
                nameof(behaviorType));
        }

        foreach (Type serviceType in implemented)
        {
            ClosedBehaviors.Add((serviceType, behaviorType));
        }

        return this;
    }
}
