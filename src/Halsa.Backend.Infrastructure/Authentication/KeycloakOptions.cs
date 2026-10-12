namespace Halsa.Backend.Infrastructure.Authentication;

public sealed class KeycloakOptions
{
    public const string SectionName = "Keycloak";

    /// <summary>
    /// URL del realm de personal (médicos, analistas, etc.).
    /// Ej. <c>http://localhost:8080/realms/halsa</c>.
    /// </summary>
    public string PersonalAuthority { get; set; } = string.Empty;

    /// <summary>
    /// URL del realm de pacientes.
    /// Ej. <c>http://localhost:8080/realms/halsa-pacientes</c>.
    /// </summary>
    public string PacientesAuthority { get; set; } = string.Empty;

    /// <summary>Expected <c>aud</c> claim. Con Keycloak suele ser el client id del resource server.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Client id del resource server; se usa para leer <c>resource_access[ClientId].roles</c>.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Poner en <c>false</c> solo para desarrollo local contra HTTP plano.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}
