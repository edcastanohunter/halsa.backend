using System.Text.RegularExpressions;

namespace Halsa.Backend.SharedKernel.Tenancy;

/// <summary>
/// Código de habilitación REPS de la IPS.
/// Formato: <c>^[0-9]{12}$</c> (ej. <c>"110011234501"</c>).
/// </summary>
public readonly record struct IpsId(string Value)
{
    private static readonly Regex Format =
        new(@"^[0-9]{12}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Intenta crear un <see cref="IpsId"/> a partir del string dado.
    /// Retorna <c>false</c> si el valor es nulo, vacío o no cumple el formato.
    /// </summary>
    public static bool TryCreate(string? value, out IpsId ipsId)
    {
        if (!string.IsNullOrWhiteSpace(value) && Format.IsMatch(value))
        {
            ipsId = new IpsId(value);
            return true;
        }

        ipsId = default;
        return false;
    }

    public override string ToString() => Value;
}
