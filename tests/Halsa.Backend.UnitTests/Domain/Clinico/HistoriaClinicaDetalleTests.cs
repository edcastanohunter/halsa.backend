using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.UnitTests.Domain.Clinico;

public sealed class HistoriaClinicaDetalleTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly TenantId Tenant = new("ARL_A");
    private static readonly CasoMedicoId Caso = CasoMedicoId.New();
    private static readonly DateTimeOffset Ahora = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly CampoCifrado Contenido = new([1, 2, 3], new byte[12], new byte[16], "ARL_A:CONTENIDO:v1");

    private static readonly ProfesionalRef Medico = new("sub-medico", "CC", "12345678", "RM-12345");
    private static readonly ProfesionalRef Informante = new("sub-informante", "CC", "87654321", null);

    private static Result<HistoriaClinicaDetalle> Registrar(
        TipoRegistroClinico tipo = TipoRegistroClinico.ConceptoMedico,
        ProfesionalRef? profesional = null,
        DateTimeOffset? atendidoEn = null,
        HistoriaClinicaDetalleId? corrigeA = null) =>
        HistoriaClinicaDetalle.Registrar(
            HistoriaClinicaDetalleId.New(), Tenant, Caso, tipo, profesional ?? Medico,
            atendidoEn ?? Ahora.AddHours(-2), Contenido, null, null, corrigeA, Ahora);

    // ── Registrar ─────────────────────────────────────────────────────────────

    [Fact]
    public void Registrar_ShouldSetRetenerHasta_FifteenYearsAfterRegistradoEn()
    {
        HistoriaClinicaDetalle registro = Registrar().Value;

        registro.RegistradoEn.ShouldBe(Ahora);
        registro.RetenerHasta.ShouldBe(new DateOnly(2041, 6, 1));
    }

    [Fact]
    public void Registrar_ShouldKeepCorrigeA()
    {
        var original = HistoriaClinicaDetalleId.New();

        HistoriaClinicaDetalle correccion = Registrar(corrigeA: original).Value;

        correccion.CorrigeA.ShouldBe(original);
    }

    [Fact]
    public void Registrar_ShouldLeaveCorrigeANull_ForAnOriginalRecord()
    {
        Registrar().Value.CorrigeA.ShouldBeNull();
    }

    [Fact]
    public void Registrar_ShouldAcceptProfesionalWithoutRethus_InReporteInicial()
    {
        Result<HistoriaClinicaDetalle> result = Registrar(TipoRegistroClinico.ReporteInicial, Informante);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Profesional.RegistroRethus.ShouldBeNull();
    }

    [Theory]
    [InlineData(TipoRegistroClinico.ConceptoMedico)]
    [InlineData(TipoRegistroClinico.Evolucion)]
    [InlineData(TipoRegistroClinico.ConceptoRehabilitacion)]
    [InlineData(TipoRegistroClinico.DictamenPcl)]
    [InlineData(TipoRegistroClinico.CertificadoIncapacidad)]
    public void Registrar_ShouldRejectProfesionalWithoutRethus_OutsideReporteInicial(TipoRegistroClinico tipo)
    {
        Result<HistoriaClinicaDetalle> result = Registrar(tipo, Informante);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ClinicoErrors.RethusRequerido);
    }

    [Fact]
    public void Registrar_ShouldRejectAtendidoEnInTheFuture()
    {
        Result<HistoriaClinicaDetalle> result = Registrar(atendidoEn: Ahora.AddMinutes(1));

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ClinicoErrors.AtendidoEnFuturo);
    }

    [Fact]
    public void Registrar_ShouldKeepTheEncryptedFieldsAsGiven()
    {
        HistoriaClinicaDetalle registro = Registrar().Value;

        registro.Contenido.ShouldBeSameAs(Contenido);
        registro.TenantId.ShouldBe(Tenant);
        registro.CasoMedicoId.ShouldBe(Caso);
    }
}
