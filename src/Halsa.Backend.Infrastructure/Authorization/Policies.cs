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
}
