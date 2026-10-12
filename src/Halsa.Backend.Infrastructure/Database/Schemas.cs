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
}
