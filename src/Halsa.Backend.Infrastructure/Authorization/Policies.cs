using Microsoft.AspNetCore.Authorization;

namespace Halsa.Backend.Infrastructure.Authorization;

/// <summary>
/// Políticas con nombre: las que combinan un permiso con otro requisito. Un permiso solo no necesita una, porque
/// <see cref="PermissionAuthorizationPolicyProvider"/> crea la política a demanda con el nombre del permiso.
/// </summary>
public static class Policies
{
    /// <summary>
    /// Cargar una incapacidad (SPEC 07): <see cref="Permissions.IncapacidadesWrite"/> y, para las personas,
    /// <c>acr=2</c> (<see cref="StepUpRequirement"/>). Basta con que la sesión tenga MFA: sin antigüedad máxima.
    /// </summary>
    public const string IncapacidadEscritura = "politica:incapacidad-escritura";

    /// <summary>El <c>acr</c> mínimo de una persona para <see cref="IncapacidadEscritura"/>.</summary>
    public const int AcrIncapacidadEscritura = 2;

    /// <summary>
    /// Leer la HC y descargar un soporte (SPEC 08): <see cref="Permissions.ClinicoRead"/>, <c>acr=2</c> y el ABAC
    /// (<see cref="AccesoClinicoRequirement"/>).
    /// </summary>
    public const string ClinicoLectura = "politica:clinico-lectura";

    /// <summary>Leer el dictamen PCL (SPEC 08): <see cref="Permissions.PclDictamenRead"/>, <c>acr=2</c> de 15 min o menos y el ABAC.</summary>
    public const string DictamenPclLectura = "politica:dictamen-pcl-lectura";

    /// <summary>Registrar y notificar el dictamen PCL (SPEC 08): <see cref="Permissions.PclDictamenWrite"/>, <c>acr=2</c> de 15 min o menos y el ABAC.</summary>
    public const string DictamenPclEscritura = "politica:dictamen-pcl-escritura";

    /// <summary>El <c>acr</c> mínimo de una persona para las tres políticas clínicas.</summary>
    public const int AcrClinico = 2;

    /// <summary>La antigüedad máxima del <c>auth_time</c> para el dictamen PCL (ARQ §6.2).</summary>
    public static readonly TimeSpan MaxAntiguedadDictamen = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Las políticas clínicas sin el ABAC: el permiso y el step-up, que no dependen del caso. Es lo que el middleware
    /// evalúa en la ruta (con su <c>DENEGADO</c> y su <c>401</c> de step-up); el ABAC lo evalúa después el endpoint con
    /// <see cref="IClinicalAuthorization"/>, porque necesita el caso como recurso.
    /// </summary>
    public static AuthorizationPolicy PreviaClinica(string politica) =>
        new AuthorizationPolicyBuilder().AddRequirements(RequisitosPrevios(politica)).Build();

    internal static IAuthorizationRequirement[] RequisitosPrevios(string politica) => politica switch
    {
        ClinicoLectura =>
        [
            new PermissionRequirement(Permissions.ClinicoRead),
            new StepUpRequirement(AcrClinico, MaxAntiguedadAuth: null)
        ],
        DictamenPclLectura =>
        [
            new PermissionRequirement(Permissions.PclDictamenRead),
            new StepUpRequirement(AcrClinico, MaxAntiguedadDictamen)
        ],
        DictamenPclEscritura =>
        [
            new PermissionRequirement(Permissions.PclDictamenWrite),
            new StepUpRequirement(AcrClinico, MaxAntiguedadDictamen)
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(politica), politica, "No es una política clínica.")
    };

    /// <summary>El break-the-glass solo abre las lecturas (SPEC 08): registrar o notificar un dictamen exige la asignación.</summary>
    internal static bool AdmiteEmergencia(string politica) => politica is ClinicoLectura or DictamenPclLectura;

    /// <summary>Las tres políticas clínicas completas: el permiso, el step-up y el ABAC.</summary>
    internal static readonly string[] Clinicas = [ClinicoLectura, DictamenPclLectura, DictamenPclEscritura];
}
