using Halsa.Backend.Domain.Terminos;

namespace Halsa.Backend.UnitTests.Domain.Terminos;

/// <summary>SPEC 09, paso 2: los festivos calculados contra la lista oficial publicada para cada año.</summary>
public sealed class CalendarioColombiaTests
{
    [Fact]
    public void Festivos2026_ShouldMatchTheOfficialList()
    {
        string[] oficiales =
        [
            "2026-01-01", "2026-01-12", "2026-03-23", "2026-04-02", "2026-04-03", "2026-05-01",
            "2026-05-18", "2026-06-08", "2026-06-15", "2026-06-29", "2026-07-20", "2026-08-07",
            "2026-08-17", "2026-10-12", "2026-11-02", "2026-11-16", "2026-12-08", "2026-12-25"
        ];

        Fechas(2026).ShouldBe(oficiales);
    }

    [Fact]
    public void Festivos2027_ShouldMatchTheOfficialList()
    {
        string[] oficiales =
        [
            "2027-01-01", "2027-01-11", "2027-03-22", "2027-03-25", "2027-03-26", "2027-05-01",
            "2027-05-10", "2027-05-31", "2027-06-07", "2027-07-05", "2027-07-20", "2027-08-07",
            "2027-08-16", "2027-10-18", "2027-11-01", "2027-11-15", "2027-12-08", "2027-12-25"
        ];

        Fechas(2027).ShouldBe(oficiales);
    }

    [Theory]
    [InlineData(2024, "2024-03-31")]
    [InlineData(2025, "2025-04-20")]
    [InlineData(2026, "2026-04-05")]
    [InlineData(2027, "2027-03-28")]
    [InlineData(2028, "2028-04-16")]
    public void DomingoDePascua_ShouldBeTheGregorianEaster(int anio, string pascua) =>
        CalendarioColombia.DomingoDePascua(anio).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(pascua);

    [Fact]
    public void EveryYear_ShouldHaveOneRowPerDate_AndTheMovedOnesOnMonday()
    {
        for (int anio = 2024; anio <= 2040; anio++)
        {
            IReadOnlyList<Festivo> festivos = CalendarioColombia.Festivos(anio);

            festivos.Select(f => f.Fecha).Distinct().Count().ShouldBe(festivos.Count, $"año {anio}");
            festivos.Count.ShouldBeInRange(17, 18, $"año {anio}");
            festivos.Where(f => f.Descripcion.Contains("Ascensión", StringComparison.Ordinal)
                                || f.Descripcion.Contains("Corpus", StringComparison.Ordinal)
                                || f.Descripcion.Contains("Sagrado", StringComparison.Ordinal))
                .ShouldAllBe(f => f.Fecha.DayOfWeek == DayOfWeek.Monday);
        }
    }

    [Fact]
    public void Festivos2025_ShouldMergeSanPedroAndSagradoCorazon_OnJune30()
    {
        IReadOnlyList<Festivo> festivos = CalendarioColombia.Festivos(2025);

        festivos.Count.ShouldBe(17);
        festivos.Single(f => f.Fecha == new DateOnly(2025, 6, 30)).Descripcion
            .ShouldBe("San Pedro y San Pablo / Sagrado Corazón de Jesús");
    }

    private static string[] Fechas(int anio) =>
        [.. CalendarioColombia.Festivos(anio)
            .Select(f => f.Fecha.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))];
}
