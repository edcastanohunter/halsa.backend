using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.UnitTests.Domain.Casos;

/// <summary>SPEC 05, paso 4: las transiciones de la validación asíncrona, solo desde <c>Radicado</c>.</summary>
public sealed class CasoMedicoTransicionesTests
{
    private static readonly TenantId Tenant = new("ARL_A");
    private static readonly DateTimeOffset Ahora = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static CasoMedico Radicado()
    {
        CasoMedico caso = CasoMedico.Radicar(
            Tenant, "ATEL-ARL_A-2026-000001", TipoEvento.AccidenteTrabajo,
            new Identificacion("CC", "1020304050"), new Identificacion("NIT", "900123456"),
            Ahora.AddDays(-1), "05001", true, Ahora).Value;

        caso.ClearDomainEvents();
        return caso;
    }

    // ── Validar ───────────────────────────────────────────────────────────────

    [Fact]
    public void Validar_FromRadicado_ShouldMoveToValidado_AndRaiseItsEvent()
    {
        CasoMedico caso = Radicado();

        Result result = caso.Validar(Ahora);

        result.IsSuccess.ShouldBeTrue();
        caso.Estado.ShouldBe(EstadoCaso.Validado);
        caso.MotivoRechazo.ShouldBeNull();

        var ev = caso.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<CasoValidadoDomainEvent>();
        ev.CasoId.ShouldBe(caso.Id);
        ev.TenantId.ShouldBe("ARL_A");
        ev.Radicado.ShouldBe("ATEL-ARL_A-2026-000001");
        ev.ValidadoEn.ShouldBe(Ahora);
    }

    [Fact]
    public void Validar_Twice_ShouldFail_WithTransicionInvalida()
    {
        CasoMedico caso = Radicado();
        caso.Validar(Ahora);
        caso.ClearDomainEvents();

        Result result = caso.Validar(Ahora);

        result.Error.ShouldBe(CasoMedicoErrors.TransicionInvalida);
        caso.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Validar_FromRechazado_ShouldFail_WithTransicionInvalida()
    {
        CasoMedico caso = Radicado();
        caso.Rechazar(MotivoRechazo.NoAfiliado, null, Ahora);

        caso.Validar(Ahora).Error.ShouldBe(CasoMedicoErrors.TransicionInvalida);
        caso.Estado.ShouldBe(EstadoCaso.Rechazado);
    }

    // ── Rechazar ──────────────────────────────────────────────────────────────

    [Fact]
    public void Rechazar_AsDuplicado_ShouldKeepTheOriginalCase_AndRaiseItsEvent()
    {
        CasoMedico caso = Radicado();
        CasoMedicoId original = CasoMedicoId.New();

        Result result = caso.Rechazar(MotivoRechazo.Duplicado, original, Ahora);

        result.IsSuccess.ShouldBeTrue();
        caso.Estado.ShouldBe(EstadoCaso.Rechazado);
        caso.MotivoRechazo.ShouldBe(MotivoRechazo.Duplicado);
        caso.DuplicadoDe.ShouldBe(original);

        var ev = caso.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<CasoRechazadoDomainEvent>();
        ev.Motivo.ShouldBe(MotivoRechazo.Duplicado);
        ev.DuplicadoDe.ShouldBe(original);
        ev.RechazadoEn.ShouldBe(Ahora);
    }

    [Fact]
    public void Rechazar_AsNoAfiliado_ShouldLeaveDuplicadoDeEmpty()
    {
        CasoMedico caso = Radicado();

        caso.Rechazar(MotivoRechazo.NoAfiliado, null, Ahora).IsSuccess.ShouldBeTrue();

        caso.MotivoRechazo.ShouldBe(MotivoRechazo.NoAfiliado);
        caso.DuplicadoDe.ShouldBeNull();
    }

    [Fact]
    public void Rechazar_FromValidado_ShouldFail_WithTransicionInvalida()
    {
        CasoMedico caso = Radicado();
        caso.Validar(Ahora);
        caso.ClearDomainEvents();

        Result result = caso.Rechazar(MotivoRechazo.Duplicado, CasoMedicoId.New(), Ahora);

        result.Error.ShouldBe(CasoMedicoErrors.TransicionInvalida);
        caso.Estado.ShouldBe(EstadoCaso.Validado);
        caso.MotivoRechazo.ShouldBeNull();
        caso.DomainEvents.ShouldBeEmpty();
    }
}
