namespace Halsa.Backend.Infrastructure.Authorization;

/// <summary>
/// Permisos atómicos del sistema. Cada constante corresponde a un client role del cliente
/// <c>halsa-api</c> en Keycloak y a una política de autorización registrada dinámicamente
/// por <see cref="PermissionAuthorizationPolicyProvider"/>.
///
/// Nomenclatura: <c>recurso:acción</c>.
/// Los roles compuestos (<c>analista-arl</c>, <c>medico-laboral</c>, …) se definen en Keycloak
/// y agrupan estos permisos atómicos.
/// </summary>
public static class Permissions
{
    // ── Casos ────────────────────────────────────────────────────────────────
    public const string CasosRead = "casos:read";
    public const string CasosWrite = "casos:write";

    /// <summary>
    /// Asignar y desasignar personal a un caso (SPEC 08). Lo tienen <c>analista-arl</c> y <c>coordinador-ips</c>;
    /// la IPS solo sobre los casos que tiene remitidos.
    /// </summary>
    public const string CasosAsignar = "casos:asignar";

    // ── Incapacidades ────────────────────────────────────────────────────────
    public const string IncapacidadesRead = "incapacidades:read";
    public const string IncapacidadesWrite = "incapacidades:write";

    // ── Plazos ───────────────────────────────────────────────────────────────
    public const string PlazosRead = "plazos:read";

    // ── Clínico ──────────────────────────────────────────────────────────────
    public const string ClinicoRead = "clinico:read";
    public const string ClinicoWrite = "clinico:write";

    /// <summary>
    /// Permiso de acceso de emergencia al expediente clínico (break-the-glass).
    /// No forma parte de ningún rol compuesto; se otorga usuario por usuario (SPEC 08).
    /// </summary>
    public const string ClinicoEmergencia = "clinico:emergencia";

    // ── PCL / Dictamen ───────────────────────────────────────────────────────
    public const string PclDictamenRead = "pcl:dictamen:read";
    public const string PclDictamenWrite = "pcl:dictamen:write";

    // ── Auditoría ────────────────────────────────────────────────────────────
    public const string AuditoriaRead = "auditoria:read";
    public const string AuditoriaExport = "auditoria:export";

    /// <summary>Revisar y cerrar los accesos de emergencia (SPEC 08). Separa revisar de consultar.</summary>
    public const string AuditoriaRevisar = "auditoria:revisar";

    // ── Integración ──────────────────────────────────────────────────────────
    public const string IntegracionFuratWrite = "integracion:furat:write";
    public const string IntegracionFurepWrite = "integracion:furep:write";
    public const string IntegracionClinicoWrite = "integracion:clinico:write";

    // ── Webhooks ─────────────────────────────────────────────────────────────
    public const string WebhooksManage = "webhooks:manage";


}
