using System.Security.Claims;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Infrastructure.Authentication;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Halsa.Backend.UnitTests.Authentication;

/// <summary>SPEC 06: the professional comes from the token's flat claims, as JwtBearer leaves them.</summary>
public sealed class ProfesionalActualTests
{
    private const string Sub = "01999000-0000-7000-8000-000000000c01";

    private static ProfesionalActual For(params Claim[] claims) =>
        new(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"))
            }
        });

    private static Claim[] Completos() =>
    [
        new("sub", Sub),
        new("rethus", "RM-DEV-0002"),
        new("tipo_documento", "CC"),
        new("numero_documento", "80000222")
    ];

    [Fact]
    public void Obtener_ShouldBuildTheProfesionalRef_FromSubAndTheProfessionalClaims()
    {
        Result<ProfesionalRef> result = For(Completos()).Obtener();

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(new ProfesionalRef(Sub, "CC", "80000222", "RM-DEV-0002"));
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("rethus")]
    [InlineData("tipo_documento")]
    [InlineData("numero_documento")]
    public void Obtener_ShouldFailWithProfesionalSinRegistro_WhenAClaimIsMissing(string faltante)
    {
        Result<ProfesionalRef> result = For([.. Completos().Where(c => c.Type != faltante)]).Obtener();

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ClinicoErrors.ProfesionalSinRegistro);
    }

    [Fact]
    public void Obtener_ShouldTreatABlankClaimAsMissing()
    {
        Claim[] claims = [.. Completos().Where(c => c.Type != "rethus"), new Claim("rethus", "  ")];

        For(claims).Obtener().Error.ShouldBe(ClinicoErrors.ProfesionalSinRegistro);
    }

    [Fact]
    public void Obtener_ShouldFail_WithoutAnHttpContext()
    {
        new ProfesionalActual(new HttpContextAccessor()).Obtener().Error.ShouldBe(ClinicoErrors.ProfesionalSinRegistro);
    }
}
