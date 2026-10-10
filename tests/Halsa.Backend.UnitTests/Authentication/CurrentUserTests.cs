using System.Security.Claims;
using Halsa.Backend.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http;

namespace Halsa.Backend.UnitTests.Authentication;

/// <summary>The principal is built exactly as JwtBearer leaves it with MapInboundClaims = false.</summary>
public sealed class CurrentUserTests
{
    private static CurrentUser For(params Claim[] claims) =>
        new(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"))
            }
        });

    [Fact]
    public void UserIdAndEmail_ShouldComeFromTheRawKeycloakClaims()
    {
        var subject = Guid.NewGuid();

        CurrentUser user = For(new Claim("sub", subject.ToString()), new Claim("email", "dev@halsa.co"));

        user.UserId.ShouldBe(subject);
        user.Email.ShouldBe("dev@halsa.co");
    }

    [Fact]
    public void UserId_ShouldBeEmpty_WhenTheSubjectIsMissing()
    {
        For(new Claim("email", "dev@halsa.co")).UserId.ShouldBe(Guid.Empty);
    }
}
