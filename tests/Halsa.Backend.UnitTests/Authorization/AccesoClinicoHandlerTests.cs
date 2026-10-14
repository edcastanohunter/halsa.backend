using System.Security.Claims;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;

namespace Halsa.Backend.UnitTests.Authorization;

/// <summary>SPEC 08, step 2: the clinical ABAC, one test per row of the rules table.</summary>
public sealed class AccesoClinicoHandlerTests
{
    private const string Sub = "01999000-0000-7000-8000-000000000a02";

    private static readonly CasoMedicoId Caso = new(Guid.CreateVersion7());
    private static readonly TenantId ArlA = new("ARL_A");
    private static readonly TenantId ArlB = new("ARL_B");
    private static readonly IpsId Ips = new("110011234501");
    private static readonly IpsId OtraIps = new("110011234502");

    private readonly ICasoAccessReader _reader = Substitute.For<ICasoAccessReader>();

    private static ITenantContext Actor(ActorTipo tipo, TenantId? tenant = null, IpsId? ips = null)
    {
        ITenantContext actor = Substitute.For<ITenantContext>();
        actor.Resuelto.Returns(true);
        actor.Tipo.Returns(tipo);
        actor.Sub.Returns(Sub);
        actor.TenantId.Returns(tenant);
        actor.IpsId.Returns(ips);
        return actor;
    }

    private void ElCasoEs(TenantId tenant, IpsId[] remisiones, params (string Sub, IpsId? IpsId)[] asignados) =>
        _reader.ObtenerAsync(Caso, Arg.Any<CancellationToken>())
            .Returns(new CasoAcceso(tenant, remisiones.ToHashSet(), asignados));

    private async Task<AuthorizationHandlerContext> EvaluarAsync(ITenantContext actor, object? resource = null)
    {
        var context = new AuthorizationHandlerContext(
            [new AccesoClinicoRequirement()], new ClaimsPrincipal(new ClaimsIdentity("Bearer")), resource ?? Caso);

        await new AccesoClinicoHandler(actor, _reader).HandleAsync(context);

        return context;
    }

    private static void ShouldFailWith(AuthorizationHandlerContext context, Error error, TenantId? tenant)
    {
        context.HasSucceeded.ShouldBeFalse();
        AccesoClinicoDenegado motivo = context.FailureReasons.OfType<AccesoClinicoDenegado>().ShouldHaveSingleItem();
        motivo.Error.ShouldBe(error);
        motivo.TenantDelCaso.ShouldBe(tenant);
    }

    // ── Arl ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Arl_OfTheCasesTenant_AssignedByTheArl_ShouldSucceed()
    {
        ElCasoEs(ArlA, [], (Sub, null));

        (await EvaluarAsync(Actor(ActorTipo.Arl, tenant: ArlA))).HasSucceeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Arl_OfTheCasesTenant_NotAssigned_ShouldFailWithNoAsignado()
    {
        ElCasoEs(ArlA, [], ("otro-sub", null));

        ShouldFailWith(await EvaluarAsync(Actor(ActorTipo.Arl, tenant: ArlA)), ClinicoErrors.NoAsignado, ArlA);
    }

    [Fact]
    public async Task Arl_WhoseSubWasAssignedByAnIps_ShouldFailWithNoAsignado()
    {
        ElCasoEs(ArlA, [Ips], (Sub, Ips));

        ShouldFailWith(await EvaluarAsync(Actor(ActorTipo.Arl, tenant: ArlA)), ClinicoErrors.NoAsignado, ArlA);
    }

    [Fact]
    public async Task Arl_OnACaseOfAnotherTenant_ShouldFailWithCasoNoEncontrado()
    {
        ElCasoEs(ArlB, [], (Sub, null));

        ShouldFailWith(await EvaluarAsync(Actor(ActorTipo.Arl, tenant: ArlA)), ClinicoErrors.CasoNoEncontrado, null);
    }

    // ── Ips ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ips_WithAVigenteRemision_AssignedByItself_ShouldSucceed()
    {
        ElCasoEs(ArlB, [Ips], (Sub, Ips));

        (await EvaluarAsync(Actor(ActorTipo.Ips, ips: Ips))).HasSucceeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Ips_WithAVigenteRemision_ButAssignedByTheArl_ShouldFailWithNoAsignado()
    {
        ElCasoEs(ArlA, [Ips], (Sub, null));

        ShouldFailWith(await EvaluarAsync(Actor(ActorTipo.Ips, ips: Ips)), ClinicoErrors.NoAsignado, ArlA);
    }

    [Fact]
    public async Task Ips_WithAVigenteRemision_ButAssignedByAnotherIps_ShouldFailWithNoAsignado()
    {
        ElCasoEs(ArlA, [Ips, OtraIps], (Sub, OtraIps));

        ShouldFailWith(await EvaluarAsync(Actor(ActorTipo.Ips, ips: Ips)), ClinicoErrors.NoAsignado, ArlA);
    }

    [Fact]
    public async Task Ips_WithoutAVigenteRemision_ShouldFailWithCasoNoEncontrado()
    {
        ElCasoEs(ArlA, [OtraIps], (Sub, Ips));

        ShouldFailWith(await EvaluarAsync(Actor(ActorTipo.Ips, ips: Ips)), ClinicoErrors.CasoNoEncontrado, null);
    }

    // ── Case not visible ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(ActorTipo.Arl)]
    [InlineData(ActorTipo.Ips)]
    public async Task ACaseTheReaderDoesNotSee_ShouldFailWithCasoNoEncontrado(ActorTipo tipo)
    {
        _reader.ObtenerAsync(Caso, Arg.Any<CancellationToken>()).Returns((CasoAcceso?)null);

        ShouldFailWith(await EvaluarAsync(Actor(tipo, ArlA, Ips)), ClinicoErrors.CasoNoEncontrado, null);
    }

    // ── Integracion, Paciente, Sistema ────────────────────────────────────────

    [Theory]
    [InlineData(ActorTipo.Integracion)]
    [InlineData(ActorTipo.Paciente)]
    [InlineData(ActorTipo.Sistema)]
    public async Task AnyOtherActor_ShouldNeverSucceed_NorReadTheCase(ActorTipo tipo)
    {
        ElCasoEs(ArlA, [Ips], (Sub, null), (Sub, Ips));

        ShouldFailWith(await EvaluarAsync(Actor(tipo, ArlA, Ips)), ClinicoErrors.AccesoNoPermitido, null);
        await _reader.DidNotReceiveWithAnyArgs().ObtenerAsync(default, default);
    }

    [Fact]
    public async Task AnUnresolvedContext_ShouldNeverSucceed()
    {
        ITenantContext actor = Substitute.For<ITenantContext>();
        actor.Resuelto.Returns(false);

        ShouldFailWith(await EvaluarAsync(actor), ClinicoErrors.AccesoNoPermitido, null);
    }

    // ── Resource ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutTheCaseAsResource_TheRequirementShouldStayPending()
    {
        ElCasoEs(ArlA, [], (Sub, null));

        AuthorizationHandlerContext context = await EvaluarAsync(Actor(ActorTipo.Arl, tenant: ArlA), resource: "no-es-un-caso");

        context.HasSucceeded.ShouldBeFalse();
        context.PendingRequirements.ShouldHaveSingleItem().ShouldBeOfType<AccesoClinicoRequirement>();
    }
}
