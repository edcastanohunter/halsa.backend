namespace Halsa.Backend.Application.Abstractions.Authentication;

/// <summary>
/// The caller's identity, projected from whatever the transport authenticated
/// (a Keycloak access token today, something else tomorrow).
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid UserId { get; }

    string? Email { get; }

    IReadOnlyCollection<string> Permissions { get; }

    bool HasPermission(string permission);
}
