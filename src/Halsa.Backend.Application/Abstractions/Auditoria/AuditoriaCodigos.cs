using System.Text;

namespace Halsa.Backend.Application.Abstractions.Auditoria;

/// <summary>
/// Forma en que <c>auditoria.accesos</c> guarda los enums: mayúsculas con guion bajo
/// (<see cref="ResultadoAuditoria.NoEncontrado"/> → <c>NO_ENCONTRADO</c>).
/// </summary>
public static class AuditoriaCodigos
{
    public static string Codigo(AccionAuditoria accion) => Codigo(accion.ToString());

    public static string Codigo(ResultadoAuditoria resultado) => Codigo(resultado.ToString());

    public static string Codigo(RecursoAuditoria recurso) => Codigo(recurso.ToString());

    /// <summary>El código guardado de <paramref name="valor"/>, o <c>null</c> si no es un valor de <typeparamref name="TEnum"/>.</summary>
    public static string? Normalizar<TEnum>(string? valor)
        where TEnum : struct, Enum =>
        TryParse(valor, out TEnum parsed) ? Codigo(parsed.ToString()) : null;

    /// <summary>Acepta el código guardado (<c>NO_ENCONTRADO</c>) o el nombre del enum (<c>NoEncontrado</c>).</summary>
    public static bool TryParse<TEnum>(string? valor, out TEnum resultado)
        where TEnum : struct, Enum
    {
        foreach (TEnum candidato in Enum.GetValues<TEnum>())
        {
            if (string.Equals(valor, Codigo(candidato.ToString()), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(valor, candidato.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                resultado = candidato;
                return true;
            }
        }

        resultado = default;
        return false;
    }

    private static string Codigo(string nombre)
    {
        var codigo = new StringBuilder(nombre.Length + 4);

        for (int i = 0; i < nombre.Length; i++)
        {
            if (i > 0 && char.IsUpper(nombre[i]))
            {
                codigo.Append('_');
            }

            codigo.Append(char.ToUpperInvariant(nombre[i]));
        }

        return codigo.ToString();
    }
}
