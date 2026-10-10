using System.Security.Claims;
using System.Text.Encodings.Web;
using Halsa.Backend.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.FunctionalTests.Abstractions;

/// <summary>
/// Stands in for Keycloak. Functional tests exercise routing, model binding, the pipeline and the
/// database - not the identity provider, which has its own tests upstream.
/// </summary>
internal sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    public static readonly Guid TestUserId = Guid.Parse("0195c0de-0000-7000-8000-000000000001");

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, TestUserId.ToString()),
            new(ClaimTypes.Email, "dev@example.com"),
            new("permissions", Permissions.WorkItemsRead),
            new("permissions", Permissions.WorkItemsWrite)
        ];

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
