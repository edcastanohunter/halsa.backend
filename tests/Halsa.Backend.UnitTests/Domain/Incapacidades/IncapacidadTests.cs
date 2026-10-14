using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Incapacidades;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.UnitTests.Domain.Incapacidades;

/// <summary>SPEC 07, step 4: one test per rule and per alert, chains included.</summary>
public sealed class IncapacidadTests
{
    private static readonly TenantId ArlA = new("ARL_A");
    private static readonly CasoMedicoId Caso = CasoMedicoId.New();

    /// <summary>The event: 2026-01-10 at 08:00 in Colombia.</summary>
    private static readonly CasoParaIncapacidad CasoAbierto =
        new(ArlA, new DateTimeOffset(2026, 1, 10, 8, 0, 0, TimeSpan.FromHours(-5)), EstadoCaso.EnAtencion);

    private static readonly DateOnly Evento = new(2026, 1, 10);

    private static readonly byte[] IndiceS62 = [0x62, 0x62];
    private static readonly byte[] IndiceM54 = [0x54, 0x54];

    private static readonly DateTimeOffset Ahora = new(2026, 1, 12, 15, 0, 0, TimeSpan.Zero);

    private static DatosIncapacidad Datos(
        DateOnly fechaInicio,
        int dias = 10,
        TipoIncapacidad tipo = TipoIncapacidad.Inicial,
        IncapacidadId? prorrogaDe = null,
        byte[]? indice = null) => new(
        IncapacidadId.New(),
        Caso,
        $"CERT-{Guid.NewGuid():N}"[..20],
        new IpsEmisora("110011234501", "IPS Uno"),
        tipo,
        prorrogaDe,
        fechaInicio,
        dias,
        ModalidadIncapacidad.Presencial,
        new ProfesionalIncapacidad("CC", "79111222", "Ana Ruiz", "RM-12345"),
        indice ?? IndiceS62,
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        "sub-test",
        "int-ips-110011234501");

    private static Result<Incapacidad> Registrar(
        DatosIncapacidad datos,
        IReadOnlyCollection<Incapacidad>? delCaso = null,
        CasoParaIncapacidad? caso = null,
        DateTimeOffset? ahora = null) =>
        Incapacidad.Registrar(datos, caso ?? CasoAbierto, delCaso ?? [], ParametrosIncapacidad.PorDefecto, ahora ?? Ahora);

    private static Incapacidad Ok(Result<Incapacidad> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    /// <summary>A chain: an initial and as many back-to-back extensions as asked, each of <paramref name="dias"/>.</summary>
    private static List<Incapacidad> Cadena(int prorrogas, int dias = 30)
    {
        var cadena = new List<Incapacidad> { Ok(Registrar(Datos(Evento, dias))) };

        for (int i = 0; i < prorrogas; i++)
        {
            Incapacidad anterior = cadena[^1];
            cadena.Add(Ok(Registrar(
                Datos(anterior.FechaFin.AddDays(1), dias, TipoIncapacidad.Prorroga, anterior.Id), cadena)));
        }

        return cadena;
    }

    // ── Initial ───────────────────────────────────────────────────────────────

    [Fact]
    public void Inicial_ShouldComputeTheEnd_TheAccumulatedDays_AndOpenItsChain()
    {
        DatosIncapacidad datos = Datos(new DateOnly(2026, 1, 11), dias: 10);

        Incapacidad incapacidad = Ok(Registrar(datos));

        incapacidad.FechaFin.ShouldBe(new DateOnly(2026, 1, 20));
        incapacidad.DiasAcumulados.ShouldBe(10);
        incapacidad.CadenaId.ShouldBe(datos.Id);
        incapacidad.Estado.ShouldBe(EstadoIncapacidad.Registrada);
        incapacidad.TenantId.ShouldBe(ArlA);
        incapacidad.RegistradaEn.ShouldBe(Ahora);
    }

    [Fact]
    public void OneDay_ShouldEndTheSameDay()
    {
        Ok(Registrar(Datos(Evento, dias: 1))).FechaFin.ShouldBe(Evento);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void DaysOutOfRange_ShouldFail(int dias)
    {
        Registrar(Datos(Evento, dias)).Error.ShouldBe(IncapacidadErrors.DiasFueraDeRango);
    }

    [Fact]
    public void Inicial_WithProrrogaDe_ShouldFail()
    {
        Registrar(Datos(Evento, prorrogaDe: IncapacidadId.New())).Error.ShouldBe(IncapacidadErrors.InicialConProrrogaDe);
    }

    // ── Rules ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EstadoCaso.Cerrado)]
    [InlineData(EstadoCaso.Rechazado)]
    public void CasoCerrado_ShouldFail(EstadoCaso estado)
    {
        Result<Incapacidad> result = Registrar(Datos(Evento), caso: CasoAbierto with { Estado = estado });

        result.Error.ShouldBe(IncapacidadErrors.CasoCerrado);
        result.Error.Type.ShouldBe(ErrorType.Unprocessable);
    }

    [Fact]
    public void FechaAnteriorAlEvento_ShouldFail()
    {
        Registrar(Datos(Evento.AddDays(-1))).Error.ShouldBe(IncapacidadErrors.FechaAnteriorAlEvento);
    }

    [Fact]
    public void TheEventDay_ShouldBeComparedInColombianTime()
    {
        // 2026-01-11 02:00 UTC is still 2026-01-10 in Colombia: an incapacidad from the 10th is not before it.
        CasoParaIncapacidad caso = CasoAbierto with { FechaEvento = new DateTimeOffset(2026, 1, 11, 2, 0, 0, TimeSpan.Zero) };

        Registrar(Datos(Evento), caso: caso).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(-9)] // ends on the first day of the existing one
    [InlineData(0)] // the same period
    [InlineData(9)] // starts on the last day of the existing one
    public void Solapada_ShouldFail(int desplazamiento)
    {
        Incapacidad existente = Ok(Registrar(Datos(Evento.AddDays(10), dias: 10)));

        Result<Incapacidad> result = Registrar(Datos(Evento.AddDays(10 + desplazamiento), dias: 10), [existente]);

        result.Error.ShouldBe(IncapacidadErrors.Solapada);
    }

    [Fact]
    public void BackToBack_ShouldNotOverlap()
    {
        Incapacidad existente = Ok(Registrar(Datos(Evento, dias: 10)));

        Registrar(Datos(existente.FechaFin.AddDays(1)), [existente]).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Prorroga_WithoutProrrogaDe_ShouldFail()
    {
        Registrar(Datos(Evento, tipo: TipoIncapacidad.Prorroga)).Error.ShouldBe(IncapacidadErrors.ProrrogaSinInicial);
    }

    [Fact]
    public void Prorroga_OfAnIncapacidadOutsideTheCase_ShouldFail()
    {
        Incapacidad delCaso = Ok(Registrar(Datos(Evento)));

        Result<Incapacidad> result = Registrar(
            Datos(delCaso.FechaFin.AddDays(1), tipo: TipoIncapacidad.Prorroga, prorrogaDe: IncapacidadId.New()),
            [delCaso]);

        result.Error.ShouldBe(IncapacidadErrors.ProrrogaSinInicial);
    }

    [Fact]
    public void Prorroga_OfAnIncapacidadThatIsNotTheLastOfItsChain_ShouldFail()
    {
        List<Incapacidad> cadena = Cadena(prorrogas: 1, dias: 10);

        Result<Incapacidad> result = Registrar(
            Datos(cadena[^1].FechaFin.AddDays(1), tipo: TipoIncapacidad.Prorroga, prorrogaDe: cadena[0].Id),
            cadena);

        result.Error.ShouldBe(IncapacidadErrors.ProrrogaNoEsUltima);
    }

    [Fact]
    public void Prorroga_StartingMoreThan30DaysAfterTheEnd_ShouldBeDiscontinua()
    {
        Incapacidad inicial = Ok(Registrar(Datos(Evento, dias: 10)));

        Result<Incapacidad> result = Registrar(
            Datos(inicial.FechaFin.AddDays(31), tipo: TipoIncapacidad.Prorroga, prorrogaDe: inicial.Id),
            [inicial]);

        result.Error.ShouldBe(IncapacidadErrors.ProrrogaDiscontinua);
    }

    [Fact]
    public void Prorroga_Starting30DaysAfterTheEnd_ShouldBeAccepted_AndContinueTheChain()
    {
        Incapacidad inicial = Ok(Registrar(Datos(Evento, dias: 10)));

        Incapacidad prorroga = Ok(Registrar(
            Datos(inicial.FechaFin.AddDays(30), dias: 5, tipo: TipoIncapacidad.Prorroga, prorrogaDe: inicial.Id),
            [inicial]));

        prorroga.CadenaId.ShouldBe(inicial.Id);
        prorroga.DiasAcumulados.ShouldBe(15);
    }

    [Fact]
    public void Prorroga_DiscontinuityIsAParameter()
    {
        Incapacidad inicial = Ok(Registrar(Datos(Evento, dias: 10)));
        var estricto = new ParametrosIncapacidad { MaxDiasEntreProrrogas = 5 };

        Result<Incapacidad> result = Incapacidad.Registrar(
            Datos(inicial.FechaFin.AddDays(6), tipo: TipoIncapacidad.Prorroga, prorrogaDe: inicial.Id),
            CasoAbierto, [inicial], estricto, Ahora);

        result.Error.ShouldBe(IncapacidadErrors.ProrrogaDiscontinua);
    }

    [Fact]
    public void AChain_WithADiscontinuousExtension_ShouldRejectOnlyThatOne()
    {
        List<Incapacidad> cadena = Cadena(prorrogas: 2, dias: 10);

        Result<Incapacidad> discontinua = Registrar(
            Datos(cadena[^1].FechaFin.AddDays(40), tipo: TipoIncapacidad.Prorroga, prorrogaDe: cadena[^1].Id),
            cadena);
        discontinua.Error.ShouldBe(IncapacidadErrors.ProrrogaDiscontinua);

        // The chain is still open: a continuous extension of its last link is accepted.
        Incapacidad continua = Ok(Registrar(
            Datos(cadena[^1].FechaFin.AddDays(1), tipo: TipoIncapacidad.Prorroga, prorrogaDe: cadena[^1].Id),
            cadena));
        continua.DiasAcumulados.ShouldBe(40);
        continua.CadenaId.ShouldBe(cadena[0].Id);
    }

    [Fact]
    public void Prorroga_WithAnotherDiagnosisCategory_ShouldFail()
    {
        Incapacidad inicial = Ok(Registrar(Datos(Evento, indice: IndiceS62)));

        Result<Incapacidad> result = Registrar(
            Datos(inicial.FechaFin.AddDays(1), tipo: TipoIncapacidad.Prorroga, prorrogaDe: inicial.Id, indice: IndiceM54),
            [inicial]);

        result.Error.ShouldBe(IncapacidadErrors.ProrrogaDiagnosticoNoRelacionado);
    }

    [Fact]
    public void Prorroga_IsComparedWithTheInitialOfTheChain_NotWithTheLastLink()
    {
        List<Incapacidad> cadena = Cadena(prorrogas: 1, dias: 10);

        Result<Incapacidad> result = Registrar(
            Datos(cadena[^1].FechaFin.AddDays(1), tipo: TipoIncapacidad.Prorroga, prorrogaDe: cadena[^1].Id,
                indice: IndiceS62),
            cadena);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void TwoChains_InTheSameCase_ShouldAccumulateSeparately()
    {
        Incapacidad primera = Ok(Registrar(Datos(Evento, dias: 10)));
        Incapacidad segunda = Ok(Registrar(Datos(Evento.AddDays(100), dias: 10, indice: IndiceM54), [primera]));

        Incapacidad prorroga = Ok(Registrar(
            Datos(segunda.FechaFin.AddDays(1), dias: 7, tipo: TipoIncapacidad.Prorroga, prorrogaDe: segunda.Id,
                indice: IndiceM54),
            [primera, segunda]));

        prorroga.CadenaId.ShouldBe(segunda.Id);
        prorroga.DiasAcumulados.ShouldBe(17);
    }

    // ── Alerts ────────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset CargaATiempo = new(2026, 1, 11, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ACumulado149_ShouldHaveNoAlert()
    {
        AlertasIncapacidad.Calcular(149, Evento, CargaATiempo, ParametrosIncapacidad.PorDefecto).ShouldBeEmpty();
    }

    [Fact]
    public void Acumulado150_ShouldAlert150()
    {
        AlertasIncapacidad.Calcular(150, Evento, CargaATiempo, ParametrosIncapacidad.PorDefecto)
            .ShouldBe([AlertasIncapacidad.Acumulado150Dias]);
    }

    [Fact]
    public void Acumulado180_ShouldAlert150And180()
    {
        AlertasIncapacidad.Calcular(180, Evento, CargaATiempo, ParametrosIncapacidad.PorDefecto)
            .ShouldBe([AlertasIncapacidad.Acumulado150Dias, AlertasIncapacidad.Acumulado180Dias]);
    }

    [Fact]
    public void Acumulado540_ShouldAlertTheThreeThresholds()
    {
        AlertasIncapacidad.Calcular(540, Evento, CargaATiempo, ParametrosIncapacidad.PorDefecto)
            .ShouldBe([AlertasIncapacidad.Acumulado150Dias, AlertasIncapacidad.Acumulado180Dias,
                AlertasIncapacidad.Acumulado540Dias]);
    }

    [Fact]
    public void AChainReaching150Days_ShouldAlertOnTheLinkThatReachesIt()
    {
        List<Incapacidad> cadena = Cadena(prorrogas: 4); // 5 × 30 = 150

        cadena[^2].Alertas(ParametrosIncapacidad.PorDefecto).ShouldNotContain(AlertasIncapacidad.Acumulado150Dias);
        cadena[^1].DiasAcumulados.ShouldBe(150);
        cadena[^1].Alertas(ParametrosIncapacidad.PorDefecto).ShouldContain(AlertasIncapacidad.Acumulado150Dias);
    }

    [Fact]
    public void ALoadMoreThan30DaysAfterTheStart_ShouldBeExtemporanea()
    {
        // 31 days after the 10th, in Colombian time.
        DateTimeOffset carga = new(2026, 2, 10, 12, 0, 0, TimeSpan.FromHours(-5));

        AlertasIncapacidad.Calcular(10, Evento, carga, ParametrosIncapacidad.PorDefecto)
            .ShouldBe([AlertasIncapacidad.CargaExtemporanea]);
    }

    [Fact]
    public void ALoad30DaysAfterTheStart_ShouldNotBeExtemporanea()
    {
        DateTimeOffset carga = new(2026, 2, 9, 23, 0, 0, TimeSpan.FromHours(-5));

        AlertasIncapacidad.Calcular(10, Evento, carga, ParametrosIncapacidad.PorDefecto).ShouldBeEmpty();
    }

    [Fact]
    public void Extemporanea_IsAParameter()
    {
        DateTimeOffset carga = new(2026, 1, 20, 12, 0, 0, TimeSpan.FromHours(-5));

        AlertasIncapacidad.Calcular(10, Evento, carga, new ParametrosIncapacidad { DiasCargaExtemporanea = 5 })
            .ShouldBe([AlertasIncapacidad.CargaExtemporanea]);
    }
}
