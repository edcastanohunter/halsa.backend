using System.Security.Claims;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Infrastructure.Authentication;
using Halsa.Backend.Infrastructure.Tenancy;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.UnitTests.Tenancy;

public sealed class TenantContextResolverTests
{
    private const string PersonalIss = "http://localhost:8080/realms/halsa";
    private const string PacientesIss = "http://localhost:8080/realms/halsa-pacientes";
    private const string ValidSub = "01999000-0000-7000-8000-000000000a01";
    private const string ValidTenantId = "ARL_A";
    private const string ValidIpsId = "110011234501";

    private static TenantContextResolver BuildResolver() => new(
        Options.Create(new KeycloakOptions
        {
            PersonalAuthority = PersonalIss,
            PacientesAuthority = PacientesIss,
            Audience = "halsa-api",
            ClientId = "halsa-api"
        }),
        NullLogger<TenantContextResolver>.Instance);

    private static ClaimsPrincipal MakePrincipal(params (string type, string value)[] claims)
    {
        var identity = new ClaimsIdentity(
            claims.Select(c => new Claim(c.type, c.value)),
            authenticationType: "Test");

        return new ClaimsPrincipal(identity);
    }

    // ── Paciente (por emisor) ─────────────────────────────────────────────────

    [Fact]
    public void Resolve_ShouldReturnPaciente_WhenIssIsPacientesRealm()
    {
        TenantContextResolver resolver = BuildResolver();

        ClaimsPrincipal principal = MakePrincipal(
            ("iss", PacientesIss),
            ("sub", ValidSub));

        Result<TenantContextData> result = resolver.Resolve(principal);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Tipo.ShouldBe(ActorTipo.Paciente);
        result.Value.Sub.ShouldBe(ValidSub);
        result.Value.TenantId.ShouldBeNull();
        result.Value.IpsId.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ShouldReturnPaciente_EvenWhenActorTipoArlIsInjected()
    {
        // Un token del realm pacientes con actor_tipo=arl inyectado debe resolverse como Paciente
        TenantContextResolver resolver = BuildResolver();

        ClaimsPrincipal principal = MakePrincipal(
            ("iss", PacientesIss),
            ("sub", ValidSub),
            ("actor_tipo", "arl"),
            ("tenant_id", ValidTenantId));

        Result<TenantContextData> result = resolver.Resolve(principal);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Tipo.ShouldBe(ActorTipo.Paciente);
    }

    // ── ARL ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_ShouldReturnArl_WhenActorTipoArlAndTenantIdPresent()
    {
        TenantContextResolver resolver = BuildResolver();

        ClaimsPrincipal principal = MakePrincipal(
            ("iss", PersonalIss),
            ("sub", ValidSub),
            ("actor_tipo", "arl"),
            ("tenant_id", ValidTenantId));

        Result<TenantContextData> result = resolver.Resolve(principal);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Tipo.ShouldBe(ActorTipo.Arl);
        result.Value.TenantId.ShouldBe(new TenantId(ValidTenantId));
        result.Value.IpsId.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ShouldFail_WhenActorTipoArlAndTenantIdMissing()
    {
        TenantContextResolver resolver = BuildResolver();

        ClaimsPrincipal principal = MakePrincipal(
            ("iss", PersonalIss),
            ("sub", ValidSub),
            ("actor_tipo", "arl"));

        Result<TenantContextData> result = resolver.Resolve(principal);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Tenant.ClaimsIncompletos");
    }

    [Fact]
    public void Resolve_ShouldFail_WhenActorTipoArlAndTenantIdHasBadFormat()
    {
        TenantContextResolver resolver = BuildResolver();

        ClaimsPrincipal principal = MakePrincipal(
            ("iss", PersonalIss),
            ("sub", ValidSub),
            ("actor_tipo", "arl"),
            ("tenant_id", "arl_a")); // minúsculas → inválido

        Result<TenantContextData> result = resolver.Resolve(principal);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Tenant.ClaimsIncompletos");
    }

    // ── IPS ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_ShouldReturnIps_WhenActorTipoIpsAndIpsIdPresent()
    {
        TenantContextResolver resolver = BuildResolver();

        ClaimsPrincipal principal = MakePrincipal(
            ("iss", PersonalIss),
            ("sub", ValidSub),
            ("actor_tipo", "ips"),
            ("ips_id", ValidIpsId));

        Result<TenantContextData> result = resolver.Resolve(principal);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Tipo.ShouldBe(ActorTipo.Ips);
        result.Value.TenantId.ShouldBeNull();
        result.Value.IpsId.ShouldBe(new IpsId(ValidIpsId));
    }

    [Fact]
    public void Resolve_ShouldFail_WhenActorTipoIpsAndIpsIdMissing()
    {
        TenantContextResolver resolver = BuildResolver();

        ClaimsPrincipal principal = MakePrincipal(
            ("iss", PersonalIss),
            ("sub", ValidSub),
            ("actor_tipo", "ips"));

        Result<TenantContextData> result = resolver.Resolve(principal);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Tenant.ClaimsIncompletos");
    }

    // ── Integración ───────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_ShouldReturnIntegracion_WithTenantId_WhenOnlyTenantIdProvided()
    {
        TenantContextResolver resolver = BuildResolver();

        ClaimsPrincipal principal = MakePrincipal(
            ("iss", PersonalIss),
            ("sub", ValidSub),
            ("actor_tipo", "integracion"),
            ("tenant_id", ValidTenantId));

        Result<TenantContextData> result = resolver.Resolve(principal);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Tipo.ShouldBe(ActorTipo.Integracion);
        result.Value.TenantId.ShouldBe(new TenantId(ValidTenantId));
        result.Value.IpsId.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ShouldReturnIntegracion_WithIpsId_WhenOnlyIpsIdProvided()
    {
        TenantContextResolver resolver = BuildResolver();

        ClaimsPrincipal principal = MakePrincipal(
            ("iss", PersonalIss),
            ("sub", ValidSub),
            ("actor_tipo", "integracion"),
            ("ips_id", ValidIpsId));

        Result<TenantContextData> result = resolver.Resolve(principal);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Tipo.ShouldBe(ActorTipo.Integracion);
        result.Value.TenantId.ShouldBeNull();
        result.Value.IpsId.ShouldBe(new IpsId(ValidIpsId));
    }

    [Fact]
    public void Resolve_ShouldFail_WhenIntegracionHasBothTenantIdAndIpsId()
    {
        TenantContextResolver resolver = BuildResolver();

        ClaimsPrincipal principal = MakePrincipal(
            ("iss", PersonalIss),
            ("sub", ValidSub),
            ("actor_tipo", "integracion"),
            ("tenant_id", ValidTenantId),
            ("ips_id", ValidIpsId));

        Result<TenantContextData> result = resolver.Resolve(principal);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Tenant.ClaimsIncompletos");
    }

    [Fact]
    public void Resolve_ShouldFail_WhenIntegracionHasNeitherTenantIdNorIpsId()
    {
        TenantContextResolver resolver = BuildResolver();

        ClaimsPrincipal principal = MakePrincipal(
            ("iss", PersonalIss),
            ("sub", ValidSub),
            ("actor_tipo", "integracion"));

        Result<TenantContextData> result = resolver.Resolve(principal);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Tenant.ClaimsIncompletos");
    }

    // ── actor_tipo inválido o reservado ───────────────────────────────────────

    [Theory]
    [InlineData("paciente")]
    [InlineData("sistema")]
    [InlineData("admin")]
    [InlineData("superuser")]
    [InlineData("")]
    public void Resolve_ShouldFail_WithActorTipoInvalido_WhenActorTipoIsReservedOrUnknown(string actorTipo)
    {
        TenantContextResolver resolver = BuildResolver();

        var claims = new List<(string, string)>
        {
            ("iss", PersonalIss),
            ("sub", ValidSub)
        };

        if (!string.IsNullOrEmpty(actorTipo))
            claims.Add(("actor_tipo", actorTipo));

        ClaimsPrincipal principal = MakePrincipal([.. claims]);

        Result<TenantContextData> result = resolver.Resolve(principal);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Tenant.ActorTipoInvalido");
    }

    [Fact]
    public void Resolve_ShouldFail_WithActorTipoInvalido_WhenActorTipoClaimAbsent()
    {
        TenantContextResolver resolver = BuildResolver();

        ClaimsPrincipal principal = MakePrincipal(
            ("iss", PersonalIss),
            ("sub", ValidSub));

        Result<TenantContextData> result = resolver.Resolve(principal);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Tenant.ActorTipoInvalido");
    }
}
