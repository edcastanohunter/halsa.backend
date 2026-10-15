using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Halsa.Backend.Domain.Pacientes;

/// <summary>
/// El formato visible de un código de activación (SPEC 12): <c>ABC123-K7Q2M9XD4P</c>, el id (<see cref="LargoId"/>
/// caracteres) y el secreto (<see cref="LargoSecreto"/>, unos 50 bits), en base32 de Crockford (sin <c>I</c>, <c>L</c>,
/// <c>O</c> ni <c>U</c>, que se confunden). Al leer se ignoran los espacios y las mayúsculas, y <c>O</c>, <c>I</c> y
/// <c>L</c> se leen como <c>0</c>, <c>1</c> y <c>1</c>.
/// </summary>
public static class FormatoCodigoActivacion
{
    public const int LargoId = 6;

    public const int LargoSecreto = 10;

    public const int LargoSal = 16;

    public const string Alfabeto = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Un id o un secreto nuevo, de caracteres aleatorios del <see cref="Alfabeto"/>.</summary>
    public static string Aleatorio(int largo)
    {
        Span<byte> bytes = stackalloc byte[largo];
        RandomNumberGenerator.Fill(bytes);

        // 256 is a multiple of 32: masking the low 5 bits is uniform.
        var texto = new StringBuilder(largo);
        foreach (byte b in bytes)
        {
            texto.Append(Alfabeto[b & 0x1F]);
        }

        return texto.ToString();
    }

    public static byte[] NuevaSal() => RandomNumberGenerator.GetBytes(LargoSal);

    /// <summary><c>SHA-256(sal ‖ secreto)</c>, el secreto en UTF-8: lo mismo que compara la función de canje.</summary>
    public static byte[] Hash(byte[] sal, string secreto)
    {
        ArgumentNullException.ThrowIfNull(sal);
        ArgumentNullException.ThrowIfNull(secreto);

        return SHA256.HashData([.. sal, .. Encoding.UTF8.GetBytes(secreto)]);
    }

    public static string Componer(string id, string secreto) => $"{id}-{secreto}";

    /// <summary>Separa y normaliza un código tecleado. <c>false</c> si no tiene la forma de uno.</summary>
    public static bool TryLeer(
        string? codigo,
        [NotNullWhen(true)] out string? id,
        [NotNullWhen(true)] out string? secreto)
    {
        id = null;
        secreto = null;

        if (string.IsNullOrWhiteSpace(codigo) || codigo.Length > 64)
        {
            return false;
        }

        var normalizado = new StringBuilder(LargoId + LargoSecreto + 1);
        foreach (char c in codigo)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            normalizado.Append(char.ToUpperInvariant(c) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                char otro => otro
            });
        }

        string[] partes = normalizado.ToString().Split('-');
        if (partes.Length != 2 ||
            partes[0].Length != LargoId ||
            partes[1].Length != LargoSecreto ||
            !partes[0].All(Alfabeto.Contains) ||
            !partes[1].All(Alfabeto.Contains))
        {
            return false;
        }

        id = partes[0];
        secreto = partes[1];
        return true;
    }
}
