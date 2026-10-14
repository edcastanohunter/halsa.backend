using System.Security.Claims;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;

namespace Halsa.Backend.UnitTests.Authorization;

/// <summary>SPEC 08, step 2: the endpoints' helper evaluates the clinical policy over the case and audits the denial.</summary>
public sealed class ClinicalAuthorizationTests
{
    private static readonly CasoMedicoId Caso = new(Guid.CreateVersion7());
    private static readonly ClaimsPrincipal User = new(new ClaimsIdentity("Bearer"));

    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    private void LaPoliticaResponde(AuthorizationResult result) =>
        _authorization.AuthorizeAsync(User, Caso, Policies.ClinicoLectura).Returns(result);

    private Task<Result> AutorizarAsync() =>
        new ClinicalAuthorization(_authorization, _audit).AutorizarAsync(
            User, Caso, Policies.ClinicoLectura, AccionAuditoria.Leer, RecursoAuditoria.RegistroClinico,
            CancellationToken.None);

    private static AuthorizationResult Denegado(Error error, TenantId? tenant) =>
        AuthorizationResult.Failed(AuthorizationFailure.Failed(
            [new AccesoClinicoDenegado(Substitute.For<IAuthorizationHandler>(), error, tenant)]));

    [Fact]
    public async Task WhenThePolicySucceeds_ShouldSucceed_WithoutAuditing()
    {
        LaPoliticaResponde(AuthorizationResult.Success());

        (await AutorizarAsync()).IsSuccess.ShouldBeTrue();
        await _audit.DidNotReceiveWithAnyArgs().RegistrarAsync(default, default!, default, default, default);
    }

    [Fact]
    public async Task NoAsignado_ShouldReturnTheError_AndAuditADenegadoWithTheCaseAndItsTenant()
    {
        var arlA = new TenantId("ARL_A");
        LaPoliticaResponde(Denegado(ClinicoErrors.NoAsignado, arlA));

        Result result = await AutorizarAsync();

        result.Error.ShouldBe(ClinicoErrors.NoAsignado);
        await _audit.Received(1).RegistrarAsync(
            AccionAuditoria.Leer,
            new ObjetivoAuditoria(RecursoAuditoria.RegistroClinico, Caso.Value.ToString(), Caso.Value),
            arlA,
            ResultadoAuditoria.Denegado,
            Arg.Any<CancellationToken>(),
            null);
    }

    [Fact]
    public async Task AnInvisibleCase_ShouldReturnCasoNoEncontrado_AndAuditWithoutTenant()
    {
        LaPoliticaResponde(Denegado(ClinicoErrors.CasoNoEncontrado, null));

        Result result = await AutorizarAsync();

        result.Error.ShouldBe(ClinicoErrors.CasoNoEncontrado);
        result.Error.Type.ShouldBe(ErrorType.NotFound);
        await _audit.Received(1).RegistrarAsync(
            AccionAuditoria.Leer, Arg.Is<ObjetivoAuditoria>(o => o.CasoMedicoId == Caso.Value), null,
            ResultadoAuditoria.Denegado, Arg.Any<CancellationToken>(), null);
    }

    [Fact]
    public async Task ADenialWithoutAnAbacReason_ShouldBeAForbidden_AndStillBeAudited()
    {
        LaPoliticaResponde(AuthorizationResult.Failed());

        Result result = await AutorizarAsync();

        result.Error.Type.ShouldBe(ErrorType.Forbidden);
        await _audit.Received(1).RegistrarAsync(
            AccionAuditoria.Leer, Arg.Any<ObjetivoAuditoria>(), null,
            ResultadoAuditoria.Denegado, Arg.Any<CancellationToken>(), null);
    }

    [Fact]
    public async Task WhenTheDenialCannotBeAudited_ShouldThrow()
    {
        LaPoliticaResponde(Denegado(ClinicoErrors.NoAsignado, null));
        _audit.RegistrarAsync(default, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("sin base")));

        await Should.ThrowAsync<InvalidOperationException>(AutorizarAsync);
    }

    // ── The policies ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Policies.ClinicoLectura, "clinico:read", null)]
    [InlineData(Policies.DictamenPclLectura, "pcl:dictamen:read", 15)]
    [InlineData(Policies.DictamenPclEscritura, "pcl:dictamen:write", 15)]
    public void TheMiddlewarePart_ShouldBeThePermission_AndTheStepUp(string politica, string permiso, int? maxMinutos)
    {
        IAuthorizationRequirement[] requisitos = [.. Policies.PreviaClinica(politica).Requirements];

        requisitos.Length.ShouldBe(2);
        requisitos.OfType<AccesoClinicoRequirement>().ShouldBeEmpty();
        Policies.AdmiteEmergencia(politica).ShouldBe(politica != Policies.DictamenPclEscritura);
        StepUpRequirement stepUp = requisitos.OfType<StepUpRequirement>().ShouldHaveSingleItem();
        stepUp.AcrMinimo.ShouldBe(2);
        stepUp.MaxAntiguedadAuth.ShouldBe(maxMinutos is int m ? TimeSpan.FromMinutes(m) : null);
        Policies.RequisitosPrevios(politica).OfType<PermissionRequirement>().ShouldHaveSingleItem().Permission.ShouldBe(permiso);
    }
}
