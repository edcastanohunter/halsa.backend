using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.UnitTests.Domain.Dictamenes;

/// <summary>SPEC 08, step 5: the dictamen's own rules, the case's calificación and the contract codes.</summary>
public sealed class DictamenPclTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 14);
    private static readonly TenantId Tenant = new("ARL_A");

    private static Result<DictamenPcl> Registrar(
        decimal porcentaje = 32.50m, DateOnly? estructuracion = null, DateOnly? dictamen = null) =>
        DictamenPcl.Registrar(
            DictamenPclId.New(), Tenant, new CasoMedicoId(Guid.CreateVersion7()), Guid.CreateVersion7(),
            porcentaje, OrigenPcl.Laboral, estructuracion ?? Hoy.AddDays(-30), dictamen ?? Hoy.AddDays(-1),
            "sub-calificador", "Calificador", "RM-0001", corrigeA: null, Hoy);

    // ── Registrar ─────────────────────────────────────────────────────────────

    [Fact]
    public void Registrar_ShouldBeLiveAndPendiente()
    {
        DictamenPcl dictamen = Registrar().Value;

        dictamen.Vigente.ShouldBeTrue();
        dictamen.EstadoNotificacion.ShouldBe(EstadoNotificacionDictamen.Pendiente);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(32.5)]
    public void Registrar_ShouldAcceptAPercentageFrom0To100_WithTwoDecimals(double porcentaje) =>
        Registrar((decimal)porcentaje).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    [InlineData(32.505)]
    public void Registrar_ShouldRejectAPercentageOutOfRange_OrWithMoreDecimals(double porcentaje) =>
        Registrar((decimal)porcentaje).Error.ShouldBe(DictamenErrors.PorcentajeFueraDeRango);

    [Fact]
    public void Registrar_ShouldRejectAnEstructuracionAfterTheDictamen() =>
        Registrar(estructuracion: Hoy.AddDays(-1), dictamen: Hoy.AddDays(-2)).Error.ShouldBe(DictamenErrors.FechasInvalidas);

    [Fact]
    public void Registrar_ShouldRejectAFutureDictamen() =>
        Registrar(dictamen: Hoy.AddDays(1)).Error.ShouldBe(DictamenErrors.FechasInvalidas);

    [Fact]
    public void Registrar_ShouldAcceptADictamenOfToday_StructuredTheSameDay() =>
        Registrar(estructuracion: Hoy, dictamen: Hoy).IsSuccess.ShouldBeTrue();

    // ── PonerEnFirme (SPEC 09) ────────────────────────────────────────────────

    [Fact]
    public void PonerEnFirme_FromNotificado_ShouldChangeOnce()
    {
        DictamenPcl dictamen = Registrar().Value;
        var ahora = new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.FromHours(-5));
        dictamen.Notificar(ahora.AddHours(-1), MedioNotificacion.Personal, ahora).IsSuccess.ShouldBeTrue();

        dictamen.PonerEnFirme().ShouldBeTrue();
        dictamen.EstadoNotificacion.ShouldBe(EstadoNotificacionDictamen.EnFirme);
        dictamen.PonerEnFirme().ShouldBeFalse();
    }

    [Fact]
    public void PonerEnFirme_OfAPendingDictamen_ShouldChangeNothing()
    {
        DictamenPcl dictamen = Registrar().Value;

        dictamen.PonerEnFirme().ShouldBeFalse();
        dictamen.EstadoNotificacion.ShouldBe(EstadoNotificacionDictamen.Pendiente);
    }

    // ── CorregirCon ───────────────────────────────────────────────────────────

    [Fact]
    public void CorregirCon_WhilePendiente_ShouldStopBeingLive()
    {
        DictamenPcl dictamen = Registrar().Value;
        DictamenPclId nuevo = DictamenPclId.New();

        dictamen.CorregirCon(nuevo).IsSuccess.ShouldBeTrue();

        dictamen.Vigente.ShouldBeFalse();
        dictamen.CorregidoPor.ShouldBe(nuevo);
    }

    [Fact]
    public void CorregirCon_AnAlreadyCorrectedOne_ShouldFail()
    {
        DictamenPcl dictamen = Registrar().Value;
        dictamen.CorregirCon(DictamenPclId.New());

        dictamen.CorregirCon(DictamenPclId.New()).Error.ShouldBe(DictamenErrors.CorrigeANoVigente);
    }

    // ── Notificar ─────────────────────────────────────────────────────────────

    /// <summary>Hoy at 15:00 in Colombia.</summary>
    private static readonly DateTimeOffset Ahora = new(2026, 10, 14, 15, 0, 0, TimeSpan.FromHours(-5));

    [Fact]
    public void Notificar_WhilePendiente_ShouldRecordIt()
    {
        DictamenPcl dictamen = Registrar().Value;

        dictamen.Notificar(Ahora.AddHours(-2), MedioNotificacion.Personal, Ahora).IsSuccess.ShouldBeTrue();

        dictamen.EstadoNotificacion.ShouldBe(EstadoNotificacionDictamen.Notificado);
        dictamen.NotificadoEn.ShouldBe(Ahora.AddHours(-2));
        dictamen.MedioNotificacion.ShouldBe(MedioNotificacion.Personal);
    }

    [Fact]
    public void Notificar_Twice_ShouldFailWithYaNotificado()
    {
        DictamenPcl dictamen = Registrar().Value;
        dictamen.Notificar(Ahora.AddHours(-2), MedioNotificacion.Personal, Ahora);

        dictamen.Notificar(Ahora.AddHours(-1), MedioNotificacion.Electronico, Ahora).Error.ShouldBe(DictamenErrors.YaNotificado);
    }

    [Fact]
    public void Notificar_ACorrectedOne_ShouldFail()
    {
        DictamenPcl dictamen = Registrar().Value;
        dictamen.CorregirCon(DictamenPclId.New());

        dictamen.Notificar(Ahora.AddHours(-2), MedioNotificacion.Personal, Ahora).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Notificar_InTheFuture_ShouldFail() =>
        Registrar().Value.Notificar(Ahora.AddMinutes(1), MedioNotificacion.Personal, Ahora)
            .Error.ShouldBe(DictamenErrors.NotificacionFueraDeRango);

    [Fact]
    public void Notificar_TheDayBeforeTheDictamen_InColombia_ShouldFail()
    {
        // The dictamen is dated Hoy - 1; 23:30 of Hoy - 2 in Colombia is already Hoy - 1 in UTC.
        DateTimeOffset laVispera = new(2026, 10, 12, 23, 30, 0, TimeSpan.FromHours(-5));

        Registrar().Value.Notificar(laVispera.ToUniversalTime(), MedioNotificacion.Personal, Ahora)
            .Error.ShouldBe(DictamenErrors.NotificacionFueraDeRango);
    }

    [Fact]
    public void Notificar_OnTheDictamensDate_ShouldSucceed() =>
        Registrar().Value.Notificar(new DateTimeOffset(2026, 10, 13, 0, 5, 0, TimeSpan.FromHours(-5)), MedioNotificacion.Personal, Ahora)
            .IsSuccess.ShouldBeTrue();

    // ── CasoMedico.Calificar ──────────────────────────────────────────────────

    private static CasoMedico Caso() =>
        CasoMedico.Radicar(
            Tenant, "RAD-1", TipoEvento.AccidenteTrabajo, new Identificacion("CC", "1"), new Identificacion("NIT", "2"),
            DateTimeOffset.UtcNow.AddDays(-10), "11001", false, DateTimeOffset.UtcNow).Value;

    [Fact]
    public void Calificar_ShouldKeepTheResult_AndMoveToEnCalificacionPcl()
    {
        CasoMedico caso = Caso();
        var resultado = new ResultadoCalificacion(32.50m, Hoy, "LABORAL");

        caso.Calificar(resultado).IsSuccess.ShouldBeTrue();

        caso.Calificacion.ShouldBe(resultado);
        caso.Estado.ShouldBe(EstadoCaso.EnCalificacionPcl);
    }

    [Fact]
    public void Calificar_ARejectedCase_ShouldFail()
    {
        CasoMedico caso = Caso();
        caso.Rechazar(MotivoRechazo.Duplicado, null, DateTimeOffset.UtcNow);

        caso.Calificar(new ResultadoCalificacion(10m, Hoy, "COMUN")).Error.ShouldBe(CasoMedicoErrors.TransicionInvalida);
        caso.Calificacion.ShouldBeNull();
    }

    // ── Codes ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EstadoNotificacionDictamen.Pendiente, "PENDIENTE")]
    [InlineData(EstadoNotificacionDictamen.EnControversia, "EN_CONTROVERSIA")]
    [InlineData(EstadoNotificacionDictamen.EnFirme, "EN_FIRME")]
    public void TheNotificationStates_ShouldUseTheContractsCodes(EstadoNotificacionDictamen estado, string codigo)
    {
        CodigosDictamen.Codigo(estado).ShouldBe(codigo);
        CodigosDictamen.Parse<EstadoNotificacionDictamen>(codigo).ShouldBe(estado);
    }

    [Theory]
    [InlineData(MedioNotificacion.CorreoCertificado, "CORREO_CERTIFICADO")]
    [InlineData(OrigenPcl.Comun, "COMUN")]
    public void TheOtherEnums_ShouldUseTheContractsCodes(object valor, string codigo) =>
        (valor switch
        {
            MedioNotificacion m => CodigosDictamen.Codigo(m),
            OrigenPcl o => CodigosDictamen.Codigo(o),
            _ => throw new InvalidOperationException()
        }).ShouldBe(codigo);

    [Theory]
    [InlineData("Laboral")]
    [InlineData("0")]
    [InlineData(null)]
    public void OnlyTheExactCode_ShouldParse(string? codigo) =>
        CodigosDictamen.TryParse<OrigenPcl>(codigo, out _).ShouldBeFalse();
}
