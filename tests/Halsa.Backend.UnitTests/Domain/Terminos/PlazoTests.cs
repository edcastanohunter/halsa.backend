using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.UnitTests.Domain.Terminos;

/// <summary>SPEC 09: las reglas del plazo, sin base de datos.</summary>
public sealed class PlazoTests
{
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);
    private static readonly CalculadoraTerminos Calculadora = new(CalendarioColombia.Festivos(2026).Select(f => f.Fecha));

    private static readonly ReglaTermino ReporteFurat = new(
        Guid.CreateVersion7(), CodigosRegla.ReporteFurat, CodigosRegla.CasoRadicado, 2, UnidadTermino.Habiles,
        1, UnidadTermino.Habiles, "Decreto 1295/1994, art. 62", new DateOnly(2000, 1, 1), null);

    private static readonly ReglaTermino Tope = new(
        Guid.CreateVersion7(), CodigosRegla.TopeIt180, CodigosRegla.IncapacidadRegistrada, 180, UnidadTermino.Calendario,
        30, UnidadTermino.Calendario, "Ley 776/2002, art. 3", new DateOnly(2000, 1, 1), null);

    // Monday 5 Oct 2026: expires Wednesday 7 at 23:59:59.
    private static readonly DateTimeOffset Lunes = new(2026, 10, 5, 10, 0, 0, Bogota);

    private static Plazo Reporte() => Plazo.Abrir(
        new TenantId("ARL_A"), new CasoMedicoId(Guid.CreateVersion7()), "ATEL-ARL_A-2026-000001", ReporteFurat, null,
        Lunes, Calculadora);

    [Fact]
    public void Abrir_ShouldComputeTheExpiryAndTheAlert_InUtc()
    {
        Plazo plazo = Reporte();

        plazo.Estado.ShouldBe(EstadoPlazo.Abierto);
        plazo.VenceEn.ShouldBe(new DateTimeOffset(2026, 10, 7, 23, 59, 59, Bogota));
        plazo.AlertaEn.ShouldBe(new DateTimeOffset(2026, 10, 6, 23, 59, 59, Bogota));
        plazo.VenceEn.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void EvaluarRadicacion_OnTime_ShouldBeCumplido()
    {
        Plazo plazo = Reporte();

        plazo.EvaluarRadicacion(Lunes.AddDays(1), "sistema").ShouldBeFalse();

        plazo.Estado.ShouldBe(EstadoPlazo.Cumplido);
        plazo.Extemporaneo.ShouldBeFalse();
        plazo.CumplidoEn.ShouldBe(Lunes.AddDays(1));
    }

    [Fact]
    public void EvaluarRadicacion_Late_ShouldBeVencidoAndExtemporaneo()
    {
        Plazo plazo = Reporte();

        plazo.EvaluarRadicacion(Lunes.AddDays(3), "sistema").ShouldBeTrue();

        plazo.Estado.ShouldBe(EstadoPlazo.Vencido);
        plazo.Extemporaneo.ShouldBeTrue();
        plazo.CumplidoEn.ShouldBeNull();
    }

    [Fact]
    public void Cumplir_AnOpenPlazo_OnTime_ShouldNotBeExtemporaneo()
    {
        Plazo plazo = Reporte();

        plazo.Cumplir("Reportado por teléfono", Lunes.AddHours(2), "sub-analista", Lunes.AddHours(3)).IsSuccess.ShouldBeTrue();

        plazo.Estado.ShouldBe(EstadoPlazo.Cumplido);
        plazo.Extemporaneo.ShouldBeFalse();
        plazo.NotaCumplimiento.ShouldBe("Reportado por teléfono");
        plazo.CumplidoPor.ShouldBe("sub-analista");
    }

    [Fact]
    public void Cumplir_AVencidoPlazo_ShouldBeExtemporaneo()
    {
        Plazo plazo = Reporte();
        plazo.EvaluarRadicacion(Lunes.AddDays(3), "sistema");

        plazo.Cumplir("Cumplido tarde, con soporte", Lunes.AddDays(4), "sub-analista", Lunes.AddDays(5)).IsSuccess.ShouldBeTrue();

        plazo.Estado.ShouldBe(EstadoPlazo.Cumplido);
        plazo.Extemporaneo.ShouldBeTrue();
    }

    [Fact]
    public void Cumplir_AfterTheExpiry_WhileStillOpen_ShouldBeExtemporaneo()
    {
        Plazo plazo = Reporte();

        plazo.Cumplir("Cumplido sin barrido aún", Lunes.AddDays(3), "sub-analista", Lunes.AddDays(3)).IsSuccess.ShouldBeTrue();

        plazo.Extemporaneo.ShouldBeTrue();
    }

    [Fact]
    public void Cumplir_ACumplidoPlazo_ShouldFail()
    {
        Plazo plazo = Reporte();
        plazo.EvaluarRadicacion(Lunes.AddDays(1), "sistema");

        plazo.Cumplir("Otra vez cumplido", Lunes.AddDays(2), "sub-analista", Lunes.AddDays(2)).Error
            .ShouldBe(PlazoErrors.NoCumplible);
    }

    [Fact]
    public void Cumplir_InTheFuture_ShouldFail() =>
        Reporte().Cumplir("Cumplido mañana", Lunes.AddDays(1), "sub-analista", Lunes).Error
            .ShouldBe(PlazoErrors.CumplidoEnFuturo);

    [Fact]
    public void Suspender_OnlyAnOpenPlazo()
    {
        Plazo abierto = Reporte();
        Plazo cumplido = Reporte();
        cumplido.EvaluarRadicacion(Lunes, "sistema");

        abierto.Suspender().ShouldBeTrue();
        abierto.Estado.ShouldBe(EstadoPlazo.Suspendido);
        cumplido.Suspender().ShouldBeFalse();
        cumplido.Estado.ShouldBe(EstadoPlazo.Cumplido);
    }

    [Fact]
    public void Tope_With120Days_ShouldExpire60DaysAfterTheEnd_AndAlert30DaysBefore()
    {
        var fin = new DateOnly(2026, 6, 29);

        Plazo tope = Plazo.AbrirTope(
            new TenantId("ARL_A"), new CasoMedicoId(Guid.CreateVersion7()), "ATEL-1", Tope, Guid.CreateVersion7(),
            new DateTimeOffset(2026, 3, 2, 0, 0, 0, Bogota), fin, 120);

        tope.VenceEn.ShouldBe(new DateTimeOffset(2026, 8, 28, 23, 59, 59, Bogota));
        tope.AlertaEn.ShouldBe(new DateTimeOffset(2026, 7, 29, 23, 59, 59, Bogota));
        tope.UltimaFechaFin.ShouldBe(fin);

        // A 30-day extension right after: 150 days, 30 left.
        tope.RecalcularTope(Tope, fin.AddDays(30), 150);
        tope.VenceEn.ShouldBe(new DateTimeOffset(2026, 8, 28, 23, 59, 59, Bogota));
        tope.UltimaFechaFin.ShouldBe(fin.AddDays(30));
    }
}
