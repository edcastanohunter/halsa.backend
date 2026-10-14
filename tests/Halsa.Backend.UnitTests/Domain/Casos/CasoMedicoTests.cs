using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.UnitTests.Domain.Casos;

public sealed class CasoMedicoTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly TenantId Tenant = new("arl-sura");
    private static readonly IpsId Ips1 = new("110011234501");
    private static readonly IpsId Ips2 = new("110011234502");
    private static readonly DateTimeOffset Ahora = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FechaEvento = Ahora.AddDays(-10);

    private static readonly Identificacion Trabajador = new("CC", "12345678");
    private static readonly Identificacion Empleador = new("NIT", "900123456");

    private static CasoMedico RadicarCaso(DateTimeOffset? fechaEvento = null) =>
        CasoMedico.Radicar(
            Tenant, "RAD-2026-001", TipoEvento.AccidenteTrabajo,
            Trabajador, Empleador,
            fechaEvento ?? FechaEvento,
            "11001", false, Ahora).Value;

    // ── Radicar ───────────────────────────────────────────────────────────────

    [Fact]
    public void Radicar_ShouldSucceed_WhenFechaEventoIsInThePast()
    {
        var result = CasoMedico.Radicar(
            Tenant, "RAD-2026-001", TipoEvento.AccidenteTrabajo,
            Trabajador, Empleador, FechaEvento, "11001", false, Ahora);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Radicado.ShouldBe("RAD-2026-001");
        result.Value.Estado.ShouldBe(EstadoCaso.Radicado);
        result.Value.TipoEvento.ShouldBe(TipoEvento.AccidenteTrabajo);
        result.Value.TenantId.ShouldBe(Tenant);
    }

    [Fact]
    public void Radicar_ShouldRaiseCasoRadicadoDomainEvent()
    {
        var result = CasoMedico.Radicar(
            Tenant, "RAD-2026-001", TipoEvento.AccidenteTrabajo,
            Trabajador, Empleador, FechaEvento, "11001", false, Ahora);

        result.Value.DomainEvents.ShouldHaveSingleItem();
        var ev = result.Value.DomainEvents.Single().ShouldBeOfType<CasoRadicadoDomainEvent>();
        ev.Radicado.ShouldBe("RAD-2026-001");
        ev.TenantId.ShouldBe(Tenant.Value);
        ev.TipoEvento.ShouldBe(TipoEvento.AccidenteTrabajo);
        ev.FechaEvento.ShouldBe(FechaEvento);
    }

    [Fact]
    public void Radicar_ShouldFail_WhenFechaEventoIsInTheFuture()
    {
        DateTimeOffset fechaFutura = Ahora.AddDays(1);

        var result = CasoMedico.Radicar(
            Tenant, "RAD-2026-001", TipoEvento.AccidenteTrabajo,
            Trabajador, Empleador, fechaFutura, "11001", false, Ahora);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(CasoMedicoErrors.FechaEventoFutura);
    }

    [Fact]
    public void Radicar_ShouldSucceed_WhenFechaEventoEqualsAhora()
    {
        var result = CasoMedico.Radicar(
            Tenant, "RAD-2026-001", TipoEvento.AccidenteTrabajo,
            Trabajador, Empleador, Ahora, "11001", false, Ahora);

        result.IsSuccess.ShouldBeTrue();
    }

    // ── Remitir ───────────────────────────────────────────────────────────────

    [Fact]
    public void Remitir_ShouldSucceed_WhenNoVigenteRemisionToSameIps()
    {
        CasoMedico caso = RadicarCaso();

        var result = caso.Remitir(Ips1, "sub-usuario", Ahora);

        result.IsSuccess.ShouldBeTrue();
        caso.Remisiones.ShouldHaveSingleItem();
        caso.Remisiones.Single().Vigente.ShouldBeTrue();
        caso.Remisiones.Single().IpsId.ShouldBe(Ips1);
    }

    [Fact]
    public void Remitir_ShouldFail_WhenVigenteRemisionToSameIpsAlreadyExists()
    {
        CasoMedico caso = RadicarCaso();
        caso.Remitir(Ips1, "sub-usuario", Ahora);

        var result = caso.Remitir(Ips1, "sub-usuario", Ahora);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(CasoMedicoErrors.RemisionDuplicada);
    }

    [Fact]
    public void Remitir_ShouldSucceed_ToDifferentIps_EvenWithVigenteToOtherIps()
    {
        CasoMedico caso = RadicarCaso();
        caso.Remitir(Ips1, "sub-usuario", Ahora);

        var result = caso.Remitir(Ips2, "sub-usuario", Ahora);

        result.IsSuccess.ShouldBeTrue();
        caso.Remisiones.Count.ShouldBe(2);
    }

    [Fact]
    public void Remitir_ShouldSucceed_AfterRevocacion_ToSameIps()
    {
        CasoMedico caso = RadicarCaso();
        var remisionId = caso.Remitir(Ips1, "sub-usuario", Ahora).Value;
        caso.RevocarRemision(remisionId, "sub-usuario", Ahora);

        // La revocada ya no es vigente, así que puede volver a remitir a Ips1.
        var result = caso.Remitir(Ips1, "sub-otro", Ahora);

        result.IsSuccess.ShouldBeTrue();
        caso.Remisiones.Count.ShouldBe(2);
        caso.Remisiones.Count(r => r.Vigente).ShouldBe(1);
    }

    // ── RevocarRemision ───────────────────────────────────────────────────────

    [Fact]
    public void RevocarRemision_ShouldSucceed_WhenRemisionExistsAndIsVigente()
    {
        CasoMedico caso = RadicarCaso();
        RemisionId remisionId = caso.Remitir(Ips1, "sub-usuario", Ahora).Value;

        var result = caso.RevocarRemision(remisionId, "sub-admin", Ahora.AddMinutes(5));

        result.IsSuccess.ShouldBeTrue();
        caso.Remisiones.Single().Vigente.ShouldBeFalse();
        caso.Remisiones.Single().RevocadaPor.ShouldBe("sub-admin");
    }

    [Fact]
    public void RevocarRemision_ShouldFail_WhenRemisionNotFound()
    {
        CasoMedico caso = RadicarCaso();
        var remisionInexistente = RemisionId.New();

        var result = caso.RevocarRemision(remisionInexistente, "sub-admin", Ahora);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(CasoMedicoErrors.RemisionNoEncontrada);
    }

    [Fact]
    public void RevocarRemision_ShouldFail_WhenRemisionAlreadyRevocada()
    {
        CasoMedico caso = RadicarCaso();
        RemisionId remisionId = caso.Remitir(Ips1, "sub-usuario", Ahora).Value;
        caso.RevocarRemision(remisionId, "sub-admin", Ahora);

        var result = caso.RevocarRemision(remisionId, "sub-admin", Ahora.AddMinutes(5));

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(CasoMedicoErrors.RemisionYaRevocada);
    }

    // ── Asignar ───────────────────────────────────────────────────────────────

    [Fact]
    public void Asignar_ShouldAddAsignacion_WhenSubNotYetAssigned()
    {
        CasoMedico caso = RadicarCaso();

        caso.Asignar("sub-clinico", null, "sub-admin", Ahora);

        caso.Asignaciones.ShouldHaveSingleItem();
        caso.Asignaciones.Single().Sub.ShouldBe("sub-clinico");
    }

    [Fact]
    public void Asignar_ShouldBeIdempotent_WhenSubAlreadyAssigned()
    {
        CasoMedico caso = RadicarCaso();
        caso.Asignar("sub-clinico", null, "sub-admin", Ahora);

        caso.Asignar("sub-clinico", null, "sub-admin", Ahora.AddMinutes(1));

        caso.Asignaciones.ShouldHaveSingleItem();
    }

    [Fact]
    public void Asignar_ShouldAllowMultipleDifferentSubs()
    {
        CasoMedico caso = RadicarCaso();

        caso.Asignar("sub-clinico-1", null, "sub-admin", Ahora);
        caso.Asignar("sub-clinico-2", null, "sub-admin", Ahora);

        caso.Asignaciones.Count.ShouldBe(2);
    }

    // ── Desasignar ────────────────────────────────────────────────────────────

    [Fact]
    public void Desasignar_ShouldRemoveAsignacion_WhenSubIsAssigned()
    {
        CasoMedico caso = RadicarCaso();
        caso.Asignar("sub-clinico", null, "sub-admin", Ahora);

        var result = caso.Desasignar("sub-clinico", null);

        result.IsSuccess.ShouldBeTrue();
        caso.Asignaciones.ShouldBeEmpty();
    }

    [Fact]
    public void Desasignar_ShouldFail_WhenSubIsNotAssigned()
    {
        CasoMedico caso = RadicarCaso();

        var result = caso.Desasignar("sub-inexistente", null);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(CasoMedicoErrors.AsignacionNoEncontrada);
    }

    [Fact]
    public void Desasignar_ShouldOnlyRemoveTargetSub_WhenMultipleAssigned()
    {
        CasoMedico caso = RadicarCaso();
        caso.Asignar("sub-clinico-1", null, "sub-admin", Ahora);
        caso.Asignar("sub-clinico-2", null, "sub-admin", Ahora);

        caso.Desasignar("sub-clinico-1", null);

        caso.Asignaciones.ShouldHaveSingleItem();
        caso.Asignaciones.Single().Sub.ShouldBe("sub-clinico-2");
    }

    [Fact]
    public void Asignar_ByAnIps_ShouldKeepTheIpsOfWhoAssigned()
    {
        CasoMedico caso = RadicarCaso();
        caso.Asignar("sub-medico", Ips1, "sub-coordinador", Ahora);

        caso.Asignaciones.Single().IpsId.ShouldBe(Ips1);
    }

    [Fact]
    public void Desasignar_ByAnIps_ShouldFail_WhenTheAssignmentIsNotItsOwn()
    {
        CasoMedico caso = RadicarCaso();
        caso.Asignar("sub-arl", null, "sub-admin", Ahora);
        caso.Asignar("sub-otra-ips", Ips2, "sub-coordinador-2", Ahora);

        caso.Desasignar("sub-arl", Ips1).Error.ShouldBe(CasoMedicoErrors.AsignacionNoEncontrada);
        caso.Desasignar("sub-otra-ips", Ips1).Error.ShouldBe(CasoMedicoErrors.AsignacionNoEncontrada);
        caso.Asignaciones.Count.ShouldBe(2);
    }

    [Fact]
    public void Desasignar_ByTheArl_ShouldRemoveAnAssignmentMadeByAnIps()
    {
        CasoMedico caso = RadicarCaso();
        caso.Asignar("sub-medico", Ips1, "sub-coordinador", Ahora);

        caso.Desasignar("sub-medico", null).IsSuccess.ShouldBeTrue();
        caso.Asignaciones.ShouldBeEmpty();
    }
}
