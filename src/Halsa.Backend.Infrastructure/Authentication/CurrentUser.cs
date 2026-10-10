using System.Security.Claims;
using Halsa.Backend.Application.Abstractions.Authentication;
using Microsoft.AspNetCore.Http;

namespace Halsa.Backend.Infrastructure.Authentication;

/// <summary>
/// Projects the authenticated principal into the narrow shape the application layer asked for.
/// This is the only place in the codebase that knows what a claim is.
/// </summary>
internal sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public const string PermissionClaimType = "permissions";

    /// <summary>
    /// Raw JWT claim names. The bearer handler runs with <c>MapInboundClaims = false</c>, so
    /// <c>sub</c> never becomes <see cref="ClaimTypes.NameIdentifier"/> and must be read as is.
    /// </summary>
    public const string SubjectClaimType = "sub";

    public const string EmailClaimType = "email";

    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public Guid UserId =>
        Guid.TryParse(Principal?.FindFirstValue(SubjectClaimType), out Guid userId)
            ? userId
            : Guid.Empty;

    public string? Email => Principal?.FindFirstValue(EmailClaimType);

    public IReadOnlyCollection<string> Permissions =>
        Principal is null
            ? []
            : [.. Principal.FindAll(PermissionClaimType).Select(claim => claim.Value)];

    public bool HasPermission(string permission) =>
        Principal?.HasClaim(PermissionClaimType, permission) ?? false;
}
