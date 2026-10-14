using Halsa.Backend.Domain.Terminos;

namespace Halsa.Backend.UnitTests.Domain.Terminos;

/// <summary>SPEC 09, paso 2: días hábiles, calendario y horas sobre el calendario de Colombia.</summary>
public sealed class CalculadoraTerminosTests
{
    private static readonly TimeSpan Bogota = TimeSpan.FromHours(-5);

    private static readonly CalculadoraTerminos Calculadora = new(
        CalendarioColombia.Festivos(2026).Concat(CalendarioColombia.Festivos(2027)).Select(f => f.Fecha));

    [Fact]
    public void TwoWorkingDays_FromTheFridayBeforeAHolidayMonday_ShouldExpireOnWednesdayAt235959()
    {
        // Friday 9 Jan 2026; Monday 12 Jan is Reyes Magos.
        var viernes = new DateTimeOffset(2026, 1, 9, 16, 0, 0, Bogota);

        DateTimeOffset vence = Calculadora.Vencimiento(viernes, 2, UnidadTermino.Habiles);

        vence.ShouldBe(new DateTimeOffset(2026, 1, 14, 23, 59, 59, Bogota));
        vence.Offset.ShouldBe(Bogota);
        vence.DayOfWeek.ShouldBe(DayOfWeek.Wednesday);
    }

    [Fact]
    public void WorkingDays_ShouldUseTheLocalDate_NotTheUtcOne()
    {
        // 21:00 on Monday in Bogotá is already Tuesday in UTC.
        var lunesNoche = new DateTimeOffset(2026, 10, 5, 21, 0, 0, Bogota);

        Calculadora.Vencimiento(lunesNoche.ToUniversalTime(), 2, UnidadTermino.Habiles)
            .ShouldBe(new DateTimeOffset(2026, 10, 7, 23, 59, 59, Bogota));
    }

    [Fact]
    public void WorkingDays_ShouldSkipTheWeekend()
    {
        var jueves = new DateTimeOffset(2026, 10, 1, 10, 0, 0, Bogota);

        Calculadora.Vencimiento(jueves, 2, UnidadTermino.Habiles)
            .ShouldBe(new DateTimeOffset(2026, 10, 5, 23, 59, 59, Bogota));
    }

    [Fact]
    public void TenWorkingDays_AcrossHolyWeek_ShouldSkipThursdayAndFriday()
    {
        // Monday 30 Mar 2026; 2 and 3 Apr are Jueves and Viernes Santo.
        var lunes = new DateTimeOffset(2026, 3, 30, 9, 0, 0, Bogota);

        Calculadora.Vencimiento(lunes, 10, UnidadTermino.Habiles)
            .ShouldBe(new DateTimeOffset(2026, 4, 15, 23, 59, 59, Bogota));
    }

    [Fact]
    public void CalendarDays_ShouldCountFromTheNextDay_IgnoringHolidays()
    {
        var evento = new DateTimeOffset(2026, 12, 20, 8, 0, 0, Bogota);

        Calculadora.Vencimiento(evento, 15, UnidadTermino.Calendario)
            .ShouldBe(new DateTimeOffset(2027, 1, 4, 23, 59, 59, Bogota));
    }

    [Fact]
    public void Hours_ShouldBeExact()
    {
        var inicio = new DateTimeOffset(2026, 10, 9, 22, 15, 30, Bogota);

        Calculadora.Vencimiento(inicio, 48, UnidadTermino.Horas).ShouldBe(inicio.AddHours(48));
    }

    [Fact]
    public void WorkingDayAlert_ShouldStepBackOverTheWeekendAndHolidays_KeepingTheTime()
    {
        // Expires Tuesday 13 Jan 2026: one working day before skips Monday 12 (holiday) and the weekend.
        var vence = new DateTimeOffset(2026, 1, 13, 23, 59, 59, Bogota);

        Calculadora.Alerta(vence, 1, UnidadTermino.Habiles)
            .ShouldBe(new DateTimeOffset(2026, 1, 9, 23, 59, 59, Bogota));
    }

    [Fact]
    public void CalendarAndHourAlerts_ShouldSubtract()
    {
        var vence = new DateTimeOffset(2026, 10, 31, 23, 59, 59, Bogota);

        Calculadora.Alerta(vence, 3, UnidadTermino.Calendario).ShouldBe(vence.AddDays(-3));
        Calculadora.Alerta(vence, 6, UnidadTermino.Horas).ShouldBe(vence.AddHours(-6));
    }

    [Theory]
    [InlineData("2026-10-10", false)]
    [InlineData("2026-10-11", false)]
    [InlineData("2026-10-12", false)]
    [InlineData("2026-10-13", true)]
    public void EsHabil_ShouldExcludeWeekendsAndHolidays(string fecha, bool habil) =>
        Calculadora.EsHabil(DateOnly.Parse(fecha, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(habil);
}
