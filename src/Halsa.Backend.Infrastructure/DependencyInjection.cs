using Halsa.Backend.Application.Abstractions.Caching;
using Halsa.Backend.Application.Casos;
using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain;
using Halsa.Backend.Infrastructure.Authentication;
using Halsa.Backend.Infrastructure.Caching;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.EventSourcing;
using Halsa.Backend.Infrastructure.Events;
using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.Infrastructure.Tenancy;
using Halsa.Backend.Infrastructure.Time;
using Halsa.Backend.Mediation.DependencyInjection;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.EventSourcing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

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
            .AddAuthorizationInternal()
            .AddTenancy();
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

        services.AddScoped<TenantSessionInterceptor>();

        services.AddDbContext<ApplicationDbContext>((sp, options) => options
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, Schemas.Default);
                npgsql.EnableRetryOnFailure(maxRetryCount: 3);
            })
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>()));

        services.AddScoped<IReadDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<ICasoMedicoRepository, CasoMedicoRepository>();

        return services;
    }

    private static IServiceCollection AddEventSourcing(this IServiceCollection services)
    {
        services.AddSingleton<IEventTypeRegistry>(_ => new EventTypeRegistry(typeof(DomainAssemblyMarker).Assembly));
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
        services
            .AddOptions<CachingOptions>()
            .Bind(configuration.GetSection(CachingOptions.SectionName))
            .ValidateOnStart();

        CachingOptions caching = configuration
            .GetSection(CachingOptions.SectionName)
            .Get<CachingOptions>() ?? new CachingOptions();

        string? redis = configuration.GetConnectionString("Cache");

        // Without Redis, HybridCache runs in-process only: fine for one replica, wrong for several,
        // because a tag eviction on one replica would never reach the others.
        if (!string.IsNullOrWhiteSpace(redis))
        {
            services.AddStackExchangeRedisCache(options =>
            {
                ConfigurationOptions redisOptions = ConfigurationOptions.Parse(redis);

                // Start even if Redis is down and fail fast while it stays down: the cache is optional.
                redisOptions.AbortOnConnectFail = false;
                redisOptions.ConnectTimeout = 2_000;
                redisOptions.SyncTimeout = 1_000;
                redisOptions.AsyncTimeout = 1_000;

                options.ConfigurationOptions = redisOptions;
                options.InstanceName = "halsa.backend:";
            });
        }

        services.AddHybridCache(options =>
        {
            options.MaximumPayloadBytes = caching.MaximumPayloadBytes;
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = caching.DefaultExpiration,
                LocalCacheExpiration = caching.LocalCacheExpiration
            };
        });

        services.AddSingleton<ICacheService, CacheService>();

        return services;
    }

    private static IServiceCollection AddHealthChecksInternal(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        IHealthChecksBuilder healthChecks = services
            .AddHealthChecks()
            .AddNpgSql(configuration.GetConnectionString("Database")!, name: "postgres");

        string? redis = configuration.GetConnectionString("Cache");

        // Degraded, not Unhealthy: without Redis the API still answers, just slower.
        if (!string.IsNullOrWhiteSpace(redis))
        {
            healthChecks.AddRedis(redis, name: "redis", failureStatus: HealthStatus.Degraded);
        }

        return services;
    }

    private static IServiceCollection AddTenancy(this IServiceCollection services)
    {
        services.AddScoped<TenantContextResolver>();
        services.AddScoped<ITenantContext, HttpTenantContext>();

        return services;
    }
}
