using System.Globalization;
using System.Security.Claims;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Infrastructure.Auditoria;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Halsa.Backend.UnitTests.Authorization;

/// <summary>SPEC 07, step 1: the generic step-up (RFC 9470) that the incapacidades and the PCL dictamen require.</summary>
public sealed class StepUpHandlerTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Ahora);

    private static ITenantContext Actor(ActorTipo tipo)
    {
        ITenantContext tenant = Substitute.For<ITenantContext>();
        tenant.Resuelto.Returns(true);
        tenant.Tipo.Returns(tipo);
        return tenant;
    }

    private async Task<AuthorizationHandlerContext> EvaluarAsync(
        ActorTipo tipo, StepUpRequirement requirement, params Claim[] claims)
    {
        var context = new AuthorizationHandlerContext(
            [requirement], new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")), resource: null);

        await new StepUpHandler(Actor(tipo), _time).HandleAsync(context);

        return context;
    }

    private static Claim Acr(string valor) => new(StepUpHandler.AcrClaimType, valor);

    private static Claim AuthTime(DateTimeOffset momento) =>
        new(StepUpHandler.AuthTimeClaimType, momento.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    private static readonly StepUpRequirement Acr2 = new(2, null);

    // ── acr ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ActorTipo.Arl)]
    [InlineData(ActorTipo.Ips)]
    public async Task Person_WithAcr2_ShouldSucceed(ActorTipo tipo)
    {
        AuthorizationHandlerContext context = await EvaluarAsync(tipo, Acr2, Acr("2"));

        context.HasSucceeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(ActorTipo.Arl, "1")]
    [InlineData(ActorTipo.Ips, "1")]
    [InlineData(ActorTipo.Ips, "0")]
    [InlineData(ActorTipo.Ips, "gold")]
    public async Task Person_BelowTheMinimumAcr_ShouldFailWithStepUpRequerido(ActorTipo tipo, string acr)
    {
        AuthorizationHandlerContext context = await EvaluarAsync(tipo, Acr2, Acr(acr));

        context.HasSucceeded.ShouldBeFalse();
        StepUpRequerido motivo = context.FailureReasons.OfType<StepUpRequerido>().ShouldHaveSingleItem();
        motivo.Acr.ShouldBe(2);
        motivo.MaxAge.ShouldBeNull();
    }

    [Fact]
    public async Task Person_WithoutAcr_ShouldFailWithStepUpRequerido()
    {
        AuthorizationHandlerContext context = await EvaluarAsync(ActorTipo.Ips, Acr2);

        context.FailureReasons.OfType<StepUpRequerido>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Integracion_ShouldAlwaysSucceed_WithoutAcr()
    {
        AuthorizationHandlerContext context = await EvaluarAsync(ActorTipo.Integracion, Acr2);

        context.HasSucceeded.ShouldBeTrue();
    }

    [Fact]
    public async Task OtherActors_ShouldNotSucceed_NorAskForStepUp()
    {
        AuthorizationHandlerContext context = await EvaluarAsync(ActorTipo.Paciente, Acr2, Acr("2"));

        context.HasSucceeded.ShouldBeFalse();
        context.FailureReasons.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnresolvedTenantContext_ShouldNotSucceed()
    {
        ITenantContext anonimo = Substitute.For<ITenantContext>();
        var context = new AuthorizationHandlerContext([Acr2], new ClaimsPrincipal(), resource: null);

        await new StepUpHandler(anonimo, _time).HandleAsync(context);

        context.HasSucceeded.ShouldBeFalse();
    }

    // ── auth_time ─────────────────────────────────────────────────────────────

    private static readonly StepUpRequirement Acr2Reciente = new(2, TimeSpan.FromMinutes(15));

    [Fact]
    public async Task MaxAge_WithARecentAuthentication_ShouldSucceed()
    {
        AuthorizationHandlerContext context = await EvaluarAsync(
            ActorTipo.Arl, Acr2Reciente, Acr("2"), AuthTime(Ahora.AddMinutes(-15)));

        context.HasSucceeded.ShouldBeTrue();
    }

    [Fact]
    public async Task MaxAge_WithAnOldAuthentication_ShouldFailWithTheMaxAge()
    {
        AuthorizationHandlerContext context = await EvaluarAsync(
            ActorTipo.Arl, Acr2Reciente, Acr("2"), AuthTime(Ahora.AddMinutes(-16)));

        context.HasSucceeded.ShouldBeFalse();
        context.FailureReasons.OfType<StepUpRequerido>().ShouldHaveSingleItem().MaxAge.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task MaxAge_WithoutAuthTime_ShouldFail()
    {
        AuthorizationHandlerContext context = await EvaluarAsync(ActorTipo.Arl, Acr2Reciente, Acr("2"));

        context.FailureReasons.OfType<StepUpRequerido>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task WithoutMaxAge_AnOldAuthentication_ShouldStillSucceed()
    {
        AuthorizationHandlerContext context = await EvaluarAsync(
            ActorTipo.Ips, Acr2, Acr("2"), AuthTime(Ahora.AddDays(-1)));

        context.HasSucceeded.ShouldBeTrue();
    }

    // ── WWW-Authenticate ──────────────────────────────────────────────────────

    [Fact]
    public void WwwAuthenticate_ShouldAskForTheAcr()
    {
        var motivo = new StepUpRequerido(new StepUpHandler(Actor(ActorTipo.Ips), _time), 2, null);

        AuditingAuthorizationResultHandler.WwwAuthenticate(motivo)
            .ShouldBe("Bearer error=\"insufficient_user_authentication\", acr_values=\"2\"");
    }

    [Fact]
    public void WwwAuthenticate_ShouldAddTheMaxAge_WhenThePolicyHasOne()
    {
        var motivo = new StepUpRequerido(
            new StepUpHandler(Actor(ActorTipo.Ips), _time), 2, TimeSpan.FromMinutes(15));

        AuditingAuthorizationResultHandler.WwwAuthenticate(motivo)
            .ShouldBe("Bearer error=\"insufficient_user_authentication\", acr_values=\"2\", max_age=\"900\"");
    }
}
