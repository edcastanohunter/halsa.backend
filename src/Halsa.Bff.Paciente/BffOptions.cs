namespace Halsa.Bff.Paciente;

/// <summary>La configuración del BFF del portal del paciente (SPEC 12), sección <c>Bff</c>.</summary>
public sealed class BffOptions
{
    public const string SectionName = "Bff";

    /// <summary>La base de la API, sin <c>/v1</c>. Ej. <c>http://localhost:5600</c>.</summary>
    public string ApiBaseUrl { get; set; } = string.Empty;

    /// <summary>El realm <c>halsa-pacientes</c>. Ej. <c>http://localhost:8080/realms/halsa-pacientes</c>.</summary>
    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = "halsa-bff-paciente";

    /// <summary>El secreto del client confidencial. Nunca en <c>appsettings.json</c>: user secrets o variable de entorno.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Poner en <c>false</c> solo para desarrollo local contra Keycloak por HTTP plano.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Inactividad tras la que la sesión vence (deslizante).</summary>
    public TimeSpan Inactividad { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>La duración máxima de una sesión, aunque haya actividad.</summary>
    public TimeSpan DuracionMaxima { get; set; } = TimeSpan.FromHours(12);

    /// <summary>El access token se refresca en el servidor cuando le queda menos que esto.</summary>
    public TimeSpan MargenRefresco { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>Las rutas y los nombres fijos del BFF.</summary>
public static class Bff
{
    public const string CookieName = "__Host-halsa-paciente";

    public const string CsrfHeader = "X-Halsa-Csrf";

    public const string ApiPrefix = "/paciente/api";

    public const string BffPrefix = "/paciente/bff";

    public const string CallbackPath = "/paciente/bff/signin-oidc";

    /// <summary>La política de las rutas proxeadas: solo la cookie, y sin sesión <c>401</c> (nunca un redirect).</summary>
    public const string PoliticaSesion = "sesion-paciente";

    /// <summary>El cliente HTTP con que el BFF habla con la API.</summary>
    public const string ClienteApi = "api";

    /// <summary>Cuándo empezó la sesión, para su duración máxima: no cambia al renovarse la cookie.</summary>
    public const string InicioSesion = ".halsa.inicio";
}
