namespace Halsa.Backend.Infrastructure.Tenancy;

/// <summary>
/// Claves para almacenar y recuperar datos de tenant en <c>HttpContext.Items</c>.
/// Se usa un objeto como clave (en lugar de una cadena) para evitar colisiones accidentales.
/// </summary>
public static class TenantContextKeys
{
    /// <summary>Clave donde el middleware almacena el <see cref="Application.Abstractions.Tenancy.TenantContextData"/>.</summary>
    public static readonly object Key = new();
}
