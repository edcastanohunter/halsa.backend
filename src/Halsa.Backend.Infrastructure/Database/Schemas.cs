namespace Halsa.Backend.Infrastructure.Database;

/// <summary>
/// One schema per bounded context. Schema isolation is what keeps a modular monolith honest:
/// a module may only read its own schema, which is also what makes extraction to a service cheap.
/// </summary>
public static class Schemas
{
    public const string Default = "app";

    public const string EventStore = "event_store";

    public const string Outbox = "outbox";

    /// <summary>
    /// Bounded context ATEL (Atención en salud laboral / Salud laboral y riesgos).
    /// Owns <c>casos_medicos</c>, <c>remisiones</c> and <c>asignaciones</c>.
    /// </summary>
    public const string Atel = "atel";

    /// <summary>
    /// Detalle clínico cifrado (SPEC 03). Solo lo alcanza <c>ClinicalDbContext</c>, conectado como
    /// <c>halsa_clinico</c>; <c>halsa_app</c> no tiene <c>USAGE</c> sobre el esquema.
    /// </summary>
    public const string Clinico = "clinico";

    /// <summary>
    /// Auditoría inmutable de accesos (SPEC 04). <c>halsa_app</c> inserta y lee (RLS por tenant),
    /// <c>halsa_clinico</c> solo inserta, y nadie actualiza ni borra.
    /// </summary>
    public const string Auditoria = "auditoria";
}
