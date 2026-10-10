using Halsa.Backend.Application.Behaviors;
using Halsa.Backend.Mediation.DependencyInjection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Halsa.Backend.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Wires up the application layer: the in-process mediator, every handler in this assembly,
    /// the validators, and the pipeline behaviours in the order they should execute.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMediation(configuration =>
        {
            configuration.RegisterServicesFromAssemblyContaining(typeof(DependencyInjection));

            // Order matters: the first behaviour added is the outermost one.
            configuration.AddOpenBehavior(typeof(LoggingBehavior<,>));
            configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
            configuration.AddOpenBehavior(typeof(QueryCachingBehavior<,>));
            // A transaction behaviour, if added, goes after this line: eviction must run after the commit.
            configuration.AddOpenBehavior(typeof(CacheInvalidationBehavior<,>));
        });

        services.AddValidatorsFromAssembly(
            typeof(DependencyInjection).Assembly,
            includeInternalTypes: true);

        return services;
    }
}
