using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Application.Behaviors;
using Halsa.Backend.Application.Integraciones.Canonico;
using Halsa.Backend.Application.Pacientes;
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
            // Inside validation (a rejected request read nothing) and outside caching (a cache hit is still a read).
            configuration.AddOpenBehavior(typeof(AuditedQueryBehavior<,>));
            configuration.AddOpenBehavior(typeof(QueryCachingBehavior<,>));
            // A transaction behaviour, if added, goes after this line: eviction must run after the commit.
            configuration.AddOpenBehavior(typeof(CacheInvalidationBehavior<,>));
        });

        // One per request: the handler reports the resource's tenant, the audit behaviour reads it.
        services.AddScoped<AuditScope>();
        services.AddScoped<IAuditScope>(sp => sp.GetRequiredService<AuditScope>());

        // Ingesta (SPEC 05): un adaptador por formato de reporte; la radicación elige por tipo.
        services.AddScoped<IReporteAdapter, FuratV1Adapter>();
        services.AddScoped<IReporteAdapter, FurepV1Adapter>();

        // Portal del paciente (SPEC 12): la lectura común de sus casos y su línea de tiempo.
        services.AddScoped<PortalPaciente>();

        // Handlers of the integration events the outbox delivers in-process (SPEC 05).
        foreach (Type handler in typeof(DependencyInjection).Assembly.GetTypes()
                     .Where(type => type is { IsClass: true, IsAbstract: false }))
        {
            foreach (Type contrato in handler.GetInterfaces().Where(i =>
                         i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>)))
            {
                services.AddScoped(contrato, handler);
            }
        }

        services.AddValidatorsFromAssembly(
            typeof(DependencyInjection).Assembly,
            includeInternalTypes: true);

        return services;
    }
}
