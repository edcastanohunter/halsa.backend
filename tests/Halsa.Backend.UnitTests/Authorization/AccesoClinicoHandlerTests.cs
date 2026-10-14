using System.Security.Claims;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Halsa.Backend.Application.Abstractions.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Halsa.Backend.UnitTests.Authorization;

/// <summary>SPEC 08, steps 2 and 7: the clinical ABAC, one test per row of the rules table, and the break-the-glass.</summary>
public sealed class AccesoClinicoHandlerTests
{
    private const string Sub = "01999000-0000-7000-8000-000000000a02";

    private static readonly CasoMedicoId Caso = new(Guid.CreateVersion7());
    private static readonly TenantId ArlA = new("ARL_A");
    private static readonly TenantId ArlB = new("ARL_B");
    private static readonly IpsId Ips = new("110011234501");
    private static readonly IpsId OtraIps = new("110011234502");

    private readonly ICasoAccessReader _reader = Substitute.For<ICasoAccessReader>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly DefaultHttpContext _http = new();
    private readonly AccesoEmergencia _emergencia = new();

    private void ConEmergencia(string? motivo)
    {
        _currentUser.HasPermission(Permissions.ClinicoEmergencia).Returns(true);
        if (motivo is not null)
        {
            _http.Request.Headers[AccesoEmergencia.HeaderMotivo] = motivo;
        }
    }

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

    private async Task<AuthorizationHandlerContext> EvaluarAsync(
        ITenantContext actor, object? resource = null, bool lectura = true)
    {
        var context = new AuthorizationHandlerContext(
            [new AccesoClinicoRequirement(AdmiteEmergencia: lectura)], new ClaimsPrincipal(new ClaimsIdentity("Bearer")),
            resource ?? Caso);

        var accessor = new HttpContextAccessor { HttpContext = _http };
        await new AccesoClinicoHandler(actor, _reader, _currentUser, accessor, _emergencia).HandleAsync(context);

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

    // ── Break-the-glass (step 7) ──────────────────────────────────────────────

    private const string Motivo25 = "Paciente inconsciente en urgencias";

    [Theory]
    [InlineData(ActorTipo.Arl)]
    [InlineData(ActorTipo.Ips)]
    public async Task NotAssigned_WithClinicoEmergencia_AndAValidMotivo_ShouldSucceed_AndMarkTheEmergency(ActorTipo tipo)
    {
        ElCasoEs(ArlA, [Ips], ("otro-sub", null));
        ConEmergencia($"  {Motivo25}  ");

        AuthorizationHandlerContext context = await EvaluarAsync(Actor(tipo, ArlA, Ips));

        context.HasSucceeded.ShouldBeTrue();
        _emergencia.Motivo.ShouldBe(Motivo25);
    }

    [Theory]
    [InlineData("Diez carac")]
    [InlineData("                         ")]
    public async Task NotAssigned_WithClinicoEmergencia_AndAShortMotivo_ShouldFailWithMotivoEmergenciaInvalido(string motivo)
    {
        ElCasoEs(ArlA, [], ("otro-sub", null));
        ConEmergencia(motivo);

        ShouldFailWith(await EvaluarAsync(Actor(ActorTipo.Arl, tenant: ArlA)), ClinicoErrors.MotivoEmergenciaInvalido, ArlA);
        _emergencia.Motivo.ShouldBeNull();
    }

    [Fact]
    public async Task NotAssigned_WithClinicoEmergencia_AndAMotivoOver500_ShouldFail()
    {
        ElCasoEs(ArlA, [], ("otro-sub", null));
        ConEmergencia(new string('x', 501));

        ShouldFailWith(await EvaluarAsync(Actor(ActorTipo.Arl, tenant: ArlA)), ClinicoErrors.MotivoEmergenciaInvalido, ArlA);
    }

    [Fact]
    public async Task NotAssigned_WithClinicoEmergencia_ButWithoutTheHeader_ShouldFailWithNoAsignado()
    {
        ElCasoEs(ArlA, [], ("otro-sub", null));
        ConEmergencia(motivo: null);

        ShouldFailWith(await EvaluarAsync(Actor(ActorTipo.Arl, tenant: ArlA)), ClinicoErrors.NoAsignado, ArlA);
    }

    [Fact]
    public async Task NotAssigned_WithAMotivo_ButWithoutClinicoEmergencia_ShouldFailWithNoAsignado()
    {
        ElCasoEs(ArlA, [], ("otro-sub", null));
        _http.Request.Headers[AccesoEmergencia.HeaderMotivo] = Motivo25;

        ShouldFailWith(await EvaluarAsync(Actor(ActorTipo.Arl, tenant: ArlA)), ClinicoErrors.NoAsignado, ArlA);
        _emergencia.Motivo.ShouldBeNull();
    }

    [Fact]
    public async Task TheEmergency_ShouldNeverSkipTheTenant()
    {
        ElCasoEs(ArlB, [], ("otro-sub", null));
        ConEmergencia(Motivo25);

        ShouldFailWith(await EvaluarAsync(Actor(ActorTipo.Arl, tenant: ArlA)), ClinicoErrors.CasoNoEncontrado, null);
        _emergencia.Motivo.ShouldBeNull();
    }

    [Fact]
    public async Task TheEmergency_ShouldNeverSkipTheRemission()
    {
        ElCasoEs(ArlA, [OtraIps], ("otro-sub", null));
        ConEmergencia(Motivo25);

        ShouldFailWith(await EvaluarAsync(Actor(ActorTipo.Ips, ips: Ips)), ClinicoErrors.CasoNoEncontrado, null);
    }

    [Fact]
    public async Task TheEmergency_ShouldNotOpenAWrite()
    {
        ElCasoEs(ArlA, [], ("otro-sub", null));
        ConEmergencia(Motivo25);

        ShouldFailWith(await EvaluarAsync(Actor(ActorTipo.Arl, tenant: ArlA), lectura: false), ClinicoErrors.NoAsignado, ArlA);
        _emergencia.Motivo.ShouldBeNull();
    }

    [Fact]
    public async Task AnAssignedProfessional_WithAMotivo_ShouldNotBeAnEmergency()
    {
        ElCasoEs(ArlA, [], (Sub, null));
        ConEmergencia(Motivo25);

        (await EvaluarAsync(Actor(ActorTipo.Arl, tenant: ArlA))).HasSucceeded.ShouldBeTrue();
        _emergencia.Motivo.ShouldBeNull();
    }

    [Theory]
    [InlineData(ActorTipo.Integracion)]
    [InlineData(ActorTipo.Paciente)]
    public async Task TheEmergency_ShouldNeverOpenOtherActors(ActorTipo tipo)
    {
        ElCasoEs(ArlA, [Ips]);
        ConEmergencia(Motivo25);

        ShouldFailWith(await EvaluarAsync(Actor(tipo, ArlA, Ips)), ClinicoErrors.AccesoNoPermitido, null);
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
