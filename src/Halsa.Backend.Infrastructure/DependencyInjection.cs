using Halsa.Backend.Application.Abstractions.Archivos;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Caching;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Dictamenes;
using Halsa.Backend.Application.Auditoria.RevisionesEmergencia;
using Halsa.Backend.Application.Dictamenes;
using Halsa.Backend.Application.Abstractions.Incapacidades;
using Halsa.Backend.Application.Abstractions.Integraciones;
using Halsa.Backend.Application.Abstractions.Pacientes;
using Halsa.Backend.Application.Auditoria;
using Halsa.Backend.Application.Casos;
using Halsa.Backend.Application.Incapacidades;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Application.Plazos;
using Halsa.Backend.Application.Webhooks;
using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain;
using Halsa.Backend.Infrastructure.Archivos;
using Halsa.Backend.Infrastructure.Auditoria;
using Halsa.Backend.Infrastructure.Authentication;
using Halsa.Backend.Infrastructure.Caching;
using Halsa.Backend.Infrastructure.Clinico;
using Halsa.Backend.Infrastructure.Clinico.Crypto;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.Database.Auditoria;
using Halsa.Backend.Infrastructure.Database.Clinico;
using Halsa.Backend.Infrastructure.Database.Integraciones;
using Halsa.Backend.Infrastructure.EventSourcing;
using Halsa.Backend.Infrastructure.Events;
using Halsa.Backend.Infrastructure.Integraciones;
using Halsa.Backend.Infrastructure.Observabilidad;
using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.Infrastructure.Proceso;
using Halsa.Backend.Infrastructure.Tenancy;
using Halsa.Backend.Infrastructure.Terminos;
using Halsa.Backend.Infrastructure.Time;
using Halsa.Backend.Infrastructure.Webhooks;
using Halsa.Backend.Infrastructure.Worker;
using Halsa.Backend.Infrastructure.Worker.Jobs;
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

    public const string IngestaConnectionStringName = "Ingesta";

    public const string WorkerConnectionStringName = "Worker";

    /// <param name="rol">
    /// SPEC 09: con <see cref="RolProceso.Worker"/> o <see cref="RolProceso.Ambos"/> se registran el
    /// <see cref="OutboxProcessor"/> y Quartz; con <see cref="RolProceso.Api"/>, ningún hosted service de background.
    /// </param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        RolProceso rol)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services
            .AddServices()
            .AddPersistence(configuration)
            .AddClinico(configuration)
            .AddIngesta(configuration)
            .AddArchivos(configuration)
            .AddAuditoria()
            .AddEventSourcing()
            .AddOutbox(configuration, rol)
            .AddCaching(configuration)
            .AddHealthChecksInternal(configuration)
            .AddAuthenticationInternal(configuration)
            .AddAuthorizationInternal()
            .AddTenancy()
            .AddWebhooks(configuration)
            .AddWorker(configuration, rol);
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
        services.AddScoped<IDictamenPclRepository, DictamenPclRepository>();

        // SPEC 09: the plazos are opened and fulfilled as halsa_app; only the sweeps use halsa_worker.
        services.AddScoped<IPlazosRepository, PlazosRepository>();
        services.AddScoped<ICatalogoTerminos, CatalogoTerminos>();

        // SPEC 12: the patient portal writes as halsa_app too; the redemption goes through its SECURITY DEFINER function.
        services.AddScoped<IPacientesDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

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
        services.AddSingleton<IMensajeCipher, AesGcmMensajeCipher>();

        return services;
    }

    /// <summary>
    /// La ingesta de FURAT y FUREP (SPEC 05): su propio contexto en la cadena <c>Ingesta</c> (rol
    /// <c>halsa_ingesta</c>), que escribe la radicación completa en una transacción sin poder leer la HC.
    /// </summary>
    private static IServiceCollection AddIngesta(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString(IngestaConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{IngestaConnectionStringName}' is not configured.");

        // No retries: the radication opens its own transaction, and a retried one would run twice.
        services.AddDbContext<IngestaDbContext>((sp, options) => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>()));

        services.AddScoped<IIngestaDbContext>(sp => sp.GetRequiredService<IngestaDbContext>());
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddScoped<IRadicadoGenerator, RadicadoGenerator>();
        services.AddScoped<IIngestaAuditTrail, IngestaAuditTrail>();

        // SPEC 07: the incapacidad upload writes through the same context and role ("mixed writing").
        services.AddScoped<IIncapacidadesDbContext>(sp => sp.GetRequiredService<IngestaDbContext>());
        services.AddScoped<IDictamenesDbContext>(sp => sp.GetRequiredService<IngestaDbContext>());
        services
            .AddOptions<IncapacidadesOptions>()
            .Bind(configuration.GetSection(IncapacidadesOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<IncapacidadesOptions>>().Value);

        // The asynchronous validation: the duplicate window and the affiliation gateway. No ARL core is integrated
        // yet: outside Development and Testing the cases wait in Radicado instead of being approved by the stub.
        services
            .AddOptions<IntegracionesOptions>()
            .Bind(configuration.GetSection(IntegracionesOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<IntegracionesOptions>, IntegracionesOptionsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<IntegracionesOptions>>().Value);

        services.AddScoped<IAfiliacionGateway>(sp =>
            DevAfiliacionGateway.Admitido(sp.GetService<IHostEnvironment>()?.EnvironmentName)
                ? new DevAfiliacionGateway()
                : new AfiliacionNoIntegradaGateway());

        return services;
    }

    /// <summary>
    /// Los soportes de la SPEC 07: el antivirus (<c>clamd</c>), síncrono y que falla cerrado, y el almacenamiento cifrado.
    /// </summary>
    private static IServiceCollection AddArchivos(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AntivirusOptions>(configuration.GetSection(AntivirusOptions.SectionName));
        services.AddSingleton<IAntivirusScanner, ClamAvScanner>();

        // Encrypted with the tenant's SOPORTES DEK (IDekStore, registered by AddClinico).
        services.Configure<ArchivosOptions>(configuration.GetSection(ArchivosOptions.SectionName));
        services.AddSingleton<IFileStore, LocalEncryptedFileStore>();

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
        services.AddScoped<IRevisionesEmergenciaStore, RevisionesEmergenciaStore>();

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

    private static IServiceCollection AddOutbox(this IServiceCollection services, IConfiguration configuration, RolProceso rol)
    {
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));

        services.AddScoped<IIntegrationEventPublisher, OutboxIntegrationEventPublisher>();

        // In-process delivery to IIntegrationEventHandler<T> (SPEC 05). The outbox already gives at-least-once;
        // a broker adapter can replace this one without touching a handler.
        services.AddSingleton<IEventBus, InProcessEventBus>();

        // SPEC 09: only the Worker drains the outbox. The API enqueues and never dispatches.
        if (rol.EjecutaWorker())
        {
            services.AddHostedService<OutboxProcessor>();
        }

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
            .AddNpgSql(configuration.GetConnectionString("Database")!, name: "postgres")
            // Degraded: without clamd the API answers everything but the uploads of supports (SPEC 07).
            .AddCheck<AntivirusHealthCheck>(AntivirusHealthCheck.Nombre, failureStatus: HealthStatus.Degraded);

        // SPEC 13: the state goes to the log every minute, so Seq alerts on a component Degraded for more than 5 minutes.
        services.AddSingleton<IHealthCheckPublisher, HealthReportLogPublisher>();
        services.Configure<HealthCheckPublisherOptions>(options =>
        {
            options.Delay = TimeSpan.FromSeconds(30);
            options.Period = TimeSpan.FromMinutes(1);
        });

        string? redis = configuration.GetConnectionString("Cache");

        // Degraded, not Unhealthy: without Redis the API still answers, just slower.
        if (!string.IsNullOrWhiteSpace(redis))
        {
            healthChecks.AddRedis(redis, name: "redis", failureStatus: HealthStatus.Degraded);
        }

        return services;
    }

    /// <summary>
    /// SPEC 09: lo que solo corre en el Worker. La conexión <c>halsa_worker</c> de los barridos entre tenants (cadena
    /// <c>Worker</c>, obligatoria con ese rol) y Quartz con sus jobs. SPEC 10: el fan-out y el dispatcher de webhooks.
    /// </summary>
    private static IServiceCollection AddWorker(this IServiceCollection services, IConfiguration configuration, RolProceso rol)
    {
        if (!rol.EjecutaWorker())
        {
            return services;
        }

        string connectionString = configuration.GetConnectionString(WorkerConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{WorkerConnectionStringName}' is not configured.");

        services.AddSingleton(new BaseDatosWorker(connectionString));
        services.AddTrabajosWorker(connectionString);

        // SPEC 10: the fan-out of every public event to its subscriptions. Only here: it writes as halsa_worker.
        services.AddScoped<WebhookFanOutHandler>();
        foreach (Type contrato in WebhookFanOutHandler.Contratos)
        {
            services.AddScoped(contrato, sp => sp.GetRequiredService<WebhookFanOutHandler>());
        }

        // The only outbound HTTP client, and the dispatcher that uses it.
        services.AddSingleton<SafeWebhookHttpClient>();
        services.AddHostedService<WebhookDispatcherJob>();

        // SPEC 13: the heartbeat to the external monitor and what the Seq alerts read (runbook 07).
        services.Configure<ObservabilidadOptions>(configuration.GetSection(ObservabilidadOptions.SectionName));
        services.AddSingleton<HeartbeatHttpClient>();

        return services;
    }

    /// <summary>
    /// Los webhooks salientes (SPEC 10): la API de suscripciones, el secreto envuelto con la KEK y la validación de la URL.
    /// El fan-out y el dispatcher, que llaman hacia afuera, solo en el Worker (<see cref="AddWorker"/>).
    /// </summary>
    private static IServiceCollection AddWebhooks(this IServiceCollection services, IConfiguration configuration)
    {
        // Fails at startup, in the API and in the Worker, when the SSRF escape hatch is on outside Development/Testing.
        services
            .AddOptions<WebhooksOptions>()
            .Bind(configuration.GetSection(WebhooksOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<WebhooksOptions>>(sp =>
            new WebhooksOptionsValidator(sp.GetService<IHostEnvironment>()?.EnvironmentName));

        services.AddSingleton<WebhookSecretProtector>();
        services.AddSingleton<IWebhookSecretProtector>(sp => sp.GetRequiredService<WebhookSecretProtector>());
        services.AddSingleton<IWebhookUrlPolicy, WebhookUrlPolicy>();
        services.AddScoped<ISuscripcionesWebhookRepository, SuscripcionesWebhookRepository>();

        return services;
    }

    private static IServiceCollection AddTenancy(this IServiceCollection services)
    {
        services.AddScoped<TenantContextResolver>();
        services.AddScoped<TenantContextHolder>();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ISystemTenantScope, SystemTenantScope>();

        return services;
    }
}
