using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.Infrastructure.Worker;
using Halsa.Backend.Infrastructure.Worker.Jobs;
using Halsa.Backend.IntegrationTests.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Quartz;
using static Halsa.Backend.IntegrationTests.Integraciones.IngestaSql;
using static Halsa.Backend.IntegrationTests.Terminos.PlazosSql;

namespace Halsa.Backend.IntegrationTests.Terminos;

/// <summary>
/// SPEC 09, paso 4: <see cref="FestivosJob"/> como <c>halsa_worker</c>, y Quartz en clúster sobre el esquema
/// <c>quartz</c>. Los años son lejanos para no cruzarse con los festivos que usan los demás tests.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class FestivosJobTests(DatabaseFixture db)
{
    [Fact]
    public async Task Job_ShouldFillThreeYears_WithoutOverwritingAManualHoliday()
    {
        const int anio = 2091;
        int esperados = Enumerable.Range(anio, 3).Sum(a => CalendarioColombia.Festivos(a).Count);

        // A decree moved Christmas' description (say): the manual row must survive.
        await EjecutarComoSuperusuarioAsync(db, """
            INSERT INTO sst.festivos (fecha, descripcion, origen) VALUES (DATE '2092-12-25', 'Navidad (decreto)', 'MANUAL')
            """);

        int insertados = await Job(anio).AsegurarAsync(CancellationToken.None);

        insertados.ShouldBe(esperados - 1);
        (await ContarAsync(db, """
            SELECT count(*) FROM sst.festivos
            WHERE fecha BETWEEN DATE '2091-01-01' AND DATE '2093-12-31' AND origen = 'CALCULADO'
            """)).ShouldBe(esperados - 1);
        (await ContarAsync(db, """
            SELECT count(*) FROM sst.festivos
            WHERE fecha = DATE '2092-12-25' AND origen = 'MANUAL' AND descripcion = 'Navidad (decreto)'
            """)).ShouldBe(1);
    }

    [Fact]
    public async Task SecondRun_ShouldInsertNothing()
    {
        const int anio = 2095;
        await Job(anio).AsegurarAsync(CancellationToken.None);

        (await Job(anio).AsegurarAsync(CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task ClusteredScheduler_ShouldStartOnTheQuartzSchema_AsHalsaWorker()
    {
        var factory = db.Services.GetRequiredService<ISchedulerFactory>();
        IScheduler scheduler = await factory.GetScheduler();

        try
        {
            (await scheduler.CheckExists(FestivosJob.Key)).ShouldBeTrue();
            scheduler.SchedulerName.ShouldBe("halsa-worker");

            SchedulerMetaData metadata = await scheduler.GetMetaData();
            metadata.JobStoreClustered.ShouldBeTrue();
            metadata.JobStoreSupportsPersistence.ShouldBeTrue();
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
        }

        (await ContarAsync(db, """
            SELECT count(*) FROM quartz.qrtz_job_details WHERE sched_name = 'halsa-worker' AND job_name = 'festivos'
            """)).ShouldBe(1);
    }

    private FestivosJob Job(int anio) => new(
        db.Services.GetRequiredService<BaseDatosWorker>(),
        new FakeTimeProvider(new DateTimeOffset(anio, 6, 1, 12, 0, 0, TimeSpan.Zero)),
        NullLogger<FestivosJob>.Instance);
}
