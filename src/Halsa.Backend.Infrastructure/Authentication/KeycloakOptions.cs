namespace Halsa.Backend.Infrastructure.Authentication;

public sealed class KeycloakOptions
{
    public const string SectionName = "Keycloak";

    /// <summary>Realm URL, e.g. <c>http://localhost:8080/realms/halsa.backend</c>.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Expected <c>aud</c> claim. With Keycloak this is usually the client id.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Client id used to read <c>resource_access</c> roles.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Set to false only for local development against plain HTTP.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}
