using Halsa.Backend.Application.Abstractions.Caching;
using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Domain.WorkItems;
using Halsa.Backend.Infrastructure.Authentication;
using Halsa.Backend.Infrastructure.Caching;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.EventSourcing;
using Halsa.Backend.Infrastructure.Events;
using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.Infrastructure.Time;
using Halsa.Backend.Mediation.DependencyInjection;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.EventSourcing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Halsa.Backend.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services
            .AddServices()
            .AddPersistence(configuration)
            .AddEventSourcing()
            .AddOutbox(configuration)
            .AddCaching(configuration)
            .AddHealthChecksInternal(configuration)
            .AddAuthenticationInternal(configuration)
            .AddAuthorizationInternal();
    }

    private static IServiceCollection AddServices(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClock, SystemClock>();

        // Projections and other infrastructure-side reactions are handlers too.
        services.AddMediation(configuration =>
            configuration.RegisterServicesFromAssemblyContaining(typeof(DependencyInjection)));

        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();

        return services;
    }

    private static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("Database")
            ?? throw new InvalidOperationException("Connection string 'Database' is not configured.");

        services.AddDbContext<ApplicationDbContext>(options => options
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, Schemas.Default);
                npgsql.EnableRetryOnFailure(maxRetryCount: 3);
            })
            .UseSnakeCaseNamingConvention());

        services.AddScoped<IReadDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ApplicationDbContext>());

        return services;
    }

    private static IServiceCollection AddEventSourcing(this IServiceCollection services)
    {
        services.AddSingleton<IEventTypeRegistry>(_ => new EventTypeRegistry(typeof(WorkItem).Assembly));
        services.AddSingleton<EventSerializer>();

        services.AddScoped<IEventStore, PostgresEventStore>();
        services.AddScoped(typeof(IEventSourcedRepository<,>), typeof(EventSourcedRepository<,>));

        return services;
    }

    private static IServiceCollection AddOutbox(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));

        services.AddScoped<IIntegrationEventPublisher, OutboxIntegrationEventPublisher>();

        // Replace with a Kafka / Service Bus adapter before production.
        services.AddSingleton<IEventBus, LoggingEventBus>();

        services.AddHostedService<OutboxProcessor>();

        return services;
    }

    private static IServiceCollection AddCaching(this IServiceCollection services, IConfiguration configuration)
    {
        string? redis = configuration.GetConnectionString("Cache");

        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redis;
            options.InstanceName = "halsa.backend:";
        });

        services.AddSingleton<ICacheService, CacheService>();

        return services;
    }

    private static IServiceCollection AddHealthChecksInternal(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddHealthChecks()
            .AddNpgSql(configuration.GetConnectionString("Database")!, name: "postgres");

        return services;
    }
}
