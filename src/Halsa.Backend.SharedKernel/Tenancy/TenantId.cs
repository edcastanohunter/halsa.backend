using System.Text.RegularExpressions;

namespace Halsa.Backend.SharedKernel.Tenancy;

/// <summary>
/// Código de la ARL. Es el tenant raíz del modelo multi-tenant.
/// Formato: <c>^[A-Z0-9_]{2,20}$</c> (ej. <c>"ARL_A"</c>).
/// </summary>
public readonly record struct TenantId(string Value)
{
    private static readonly Regex Format =
        new(@"^[A-Z0-9_]{2,20}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Intenta crear un <see cref="TenantId"/> a partir del string dado.
    /// Retorna <c>false</c> si el valor es nulo, vacío o no cumple el formato.
    /// </summary>
    public static bool TryCreate(string? value, out TenantId tenantId)
    {
        if (!string.IsNullOrWhiteSpace(value) && Format.IsMatch(value))
        {
            tenantId = new TenantId(value);
            return true;
        }

        tenantId = default;
        return false;
    }

    public override string ToString() => Value;
}
