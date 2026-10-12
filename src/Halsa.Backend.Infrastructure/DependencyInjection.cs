using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Caching;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Auditoria;
using Halsa.Backend.Application.Casos;
using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain;
using Halsa.Backend.Infrastructure.Auditoria;
using Halsa.Backend.Infrastructure.Authentication;
using Halsa.Backend.Infrastructure.Caching;
using Halsa.Backend.Infrastructure.Clinico;
using Halsa.Backend.Infrastructure.Clinico.Crypto;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.Database.Auditoria;
using Halsa.Backend.Infrastructure.Database.Clinico;
using Halsa.Backend.Infrastructure.EventSourcing;
using Halsa.Backend.Infrastructure.Events;
using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.Infrastructure.Tenancy;
using Halsa.Backend.Infrastructure.Time;
using Halsa.Backend.Mediation.DependencyInjection;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.EventSourcing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Halsa.Backend.Infrastructure;

public static class DependencyInjection
{
    public const string ClinicalConnectionStringName = "Clinical";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services
            .AddServices()
            .AddPersistence(configuration)
            .AddClinico(configuration)
            .AddAuditoria()
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
                DatabaseMigrator.ConfigureApplication(npgsql);
                npgsql.EnableRetryOnFailure(maxRetryCount: 3);
            })
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>()));

        services.AddScoped<IReadDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<ICasoMedicoRepository, CasoMedicoRepository>();

        return services;
    }

    /// <summary>
    /// The clinical side (SPEC 03): its own context on the <c>Clinical</c> connection string
    /// (role <c>halsa_clinico</c>), the KEK, the per-tenant DEKs and the field cipher.
    /// </summary>
    private static IServiceCollection AddClinico(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString(ClinicalConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ClinicalConnectionStringName}' is not configured.");

        services.AddDbContext<ClinicalDbContext>((sp, options) => options
            .UseNpgsql(connectionString, npgsql =>
            {
                DatabaseMigrator.ConfigureClinical(npgsql);
                npgsql.EnableRetryOnFailure(maxRetryCount: 3);
            })
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>()));

        services.AddScoped<IClinicalDbContext>(sp => sp.GetRequiredService<ClinicalDbContext>());
        services.AddScoped<IClinicalCaseGate, ClinicalCaseGate>();

        // Fails at startup, not at the first clinical write, when the KEK is missing or misplaced.
        services
            .AddOptions<ClinicoOptions>()
            .Bind(configuration.GetSection(ClinicoOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ClinicoOptions>>(sp =>
            new ClinicoOptionsValidator(sp.GetService<IHostEnvironment>()?.EnvironmentName));

        services.AddSingleton<IKeyEncryptionProvider, LocalFileKeyEncryptionProvider>();
        services.AddSingleton<IDekStore>(sp => new DekStore(
            connectionString,
            sp.GetRequiredService<IKeyEncryptionProvider>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IClinicalCipher, AesGcmClinicalCipher>();

        return services;
    }

    /// <summary>
    /// Auditoría de accesos (SPEC 04): una trail por contexto, para que la fila caiga en la transacción
    /// de la escritura, y un writer con contexto propio para lecturas y denegados.
    /// </summary>
    private static IServiceCollection AddAuditoria(this IServiceCollection services)
    {
        services.AddScoped<IAuditActorProvider, HttpAuditActorProvider>();
        services.AddScoped<AccesoAuditadoFactory>();
        services.AddScoped<IAuditTrail, AuditTrail>();
        services.AddScoped<IClinicalAuditTrail, ClinicalAuditTrail>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IAccesosAuditadosReader, AccesosAuditadosReader>();

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
