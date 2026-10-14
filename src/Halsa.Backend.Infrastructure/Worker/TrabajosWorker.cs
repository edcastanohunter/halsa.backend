using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace Halsa.Backend.Infrastructure.Worker;

/// <summary>
/// Quartz en el Worker (SPEC 09): JobStore en PostgreSQL (esquema <c>quartz</c>, como <c>halsa_worker</c>), en clúster
/// y con serialización <c>System.Text.Json</c>. Con una réplica no cuesta nada, y escalar después no cambia el código:
/// cada disparo lo ejecuta una sola instancia.
///
/// Las horas son UTC: Colombia es UTC−5 fijo, así que las 02:00 locales son las 07:00 UTC. Así el JobStore no depende
/// de resolver <c>America/Bogota</c> en la máquina que lo lea.
/// </summary>
internal static class TrabajosWorker
{
    public const string Grupo = "halsa";

    public static IServiceCollection AddTrabajosWorker(this IServiceCollection services, string connectionString)
    {
        services.AddQuartz(quartz =>
        {
            quartz.SchedulerName = "halsa-worker";
            quartz.SchedulerId = "AUTO";

            quartz.UsePersistentStore(store =>
            {
                store.UsePostgres(postgres =>
                {
                    postgres.ConnectionString = connectionString;
                    postgres.TablePrefix = $"{Schemas.Quartz}.qrtz_";
                });
                store.UseSystemTextJsonSerializer();
                store.UseClustering();
            });

            // Every minute.
            Programar<BarridoPlazosJob>(quartz, BarridoPlazosJob.Key, "0 * * * * ?");

            // 02:00 in Colombia, and once at startup: a fresh database without holidays would open every plazo wrong
            // until the first night.
            Programar<FestivosJob>(quartz, FestivosJob.Key, "0 0 7 * * ?");
            quartz.AddTrigger(trigger => trigger
                .ForJob(FestivosJob.Key)
                .WithIdentity("festivos-al-arrancar", Grupo)
                .StartNow());

            // 02:30 in Colombia, after the holidays; FestivosJob also triggers it when it loads new ones.
            Programar<RecalcularPlazosJob>(quartz, RecalcularPlazosJob.Key, "0 30 7 * * ?");

            // 03:00 in Colombia.
            Programar<ParticionesAuditoriaJob>(quartz, ParticionesAuditoriaJob.Key, "0 0 8 * * ?");

            // Every hour.
            Programar<PurgaIdempotenciaJob>(quartz, PurgaIdempotenciaJob.Key, "0 15 * * * ?");
            Programar<ArchivosHuerfanosJob>(quartz, ArchivosHuerfanosJob.Key, "0 45 * * * ?");
        });

        // Lets a running job finish its batch on shutdown; the rest is recovered by the cluster.
        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

        return services;
    }

    private static void Programar<TJob>(IServiceCollectionQuartzConfigurator quartz, JobKey key, string cron)
        where TJob : IJob
    {
        quartz.AddJob<TJob>(job => job.WithIdentity(key).StoreDurably());
        quartz.AddTrigger(trigger => trigger
            .ForJob(key)
            .WithIdentity($"{key.Name}-trigger", Grupo)
            .WithCronSchedule(cron, cronSchedule => cronSchedule.InTimeZone(TimeZoneInfo.Utc)));
    }
}
