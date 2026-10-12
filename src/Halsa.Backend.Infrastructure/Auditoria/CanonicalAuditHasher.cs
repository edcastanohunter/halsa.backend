using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Halsa.Backend.Infrastructure.Database.Auditoria;

namespace Halsa.Backend.Infrastructure.Auditoria;

/// <summary>
/// <c>hash_registro</c>: SHA-256 de la forma canónica JCS (RFC 8785) de la fila.
///
/// JCS restringido a la forma fija del registro: un objeto plano cuyos valores son cadenas, enteros,
/// <c>null</c> o arreglos de cadenas. Para eso basta la parte de JCS que importa: claves ordenadas por
/// unidades UTF-16, sin espacios, cadenas escapadas como en ECMAScript y UTF-8. No hay números
/// decimales, que es lo único que obligaría a una librería general.
/// </summary>
internal static class CanonicalAuditHasher
{
    private const string FormatoFecha = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    public static byte[] Hash(AccesoAuditado acceso) => Hash(Campos(acceso));

    public static byte[] Hash(IEnumerable<KeyValuePair<string, object?>> campos) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalizar(campos)));

    /// <summary>Todas las columnas de la fila salvo el propio hash, con su nombre de columna.</summary>
    public static IEnumerable<KeyValuePair<string, object?>> Campos(AccesoAuditado acceso)
    {
        ArgumentNullException.ThrowIfNull(acceso);

        return new Dictionary<string, object?>
        {
            ["id"] = acceso.Id.ToString(),
            ["ocurrido_en"] = acceso.OcurridoEn.UtcDateTime.ToString(FormatoFecha, CultureInfo.InvariantCulture),
            ["tenant_id"] = acceso.TenantId,
            ["actor_id"] = acceso.ActorId,
            ["actor_tipo"] = acceso.ActorTipo,
            ["actor_tenant_id"] = acceso.ActorTenantId,
            ["actor_ips_id"] = acceso.ActorIpsId,
            ["actor_roles"] = acceso.ActorRoles,
            ["client_id"] = acceso.ClientId,
            ["ip"] = acceso.Ip?.ToString(),
            ["user_agent"] = acceso.UserAgent,
            ["correlation_id"] = acceso.CorrelationId,
            ["accion"] = acceso.Accion,
            ["resultado"] = acceso.Resultado,
            ["recurso_tipo"] = acceso.RecursoTipo,
            ["recurso_id"] = acceso.RecursoId,
            ["caso_medico_id"] = acceso.CasoMedicoId?.ToString(),
            ["motivo"] = acceso.Motivo,
            ["acr"] = acceso.Acr
        };
    }

    public static string Canonicalizar(IEnumerable<KeyValuePair<string, object?>> campos)
    {
        ArgumentNullException.ThrowIfNull(campos);

        var json = new StringBuilder();
        json.Append('{');

        bool primero = true;
        foreach (KeyValuePair<string, object?> campo in campos.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (!primero)
            {
                json.Append(',');
            }

            primero = false;
            EscribirCadena(json, campo.Key);
            json.Append(':');
            EscribirValor(json, campo.Value);
        }

        json.Append('}');
        return json.ToString();
    }

    private static void EscribirValor(StringBuilder json, object? valor)
    {
        switch (valor)
        {
            case null:
                json.Append("null");
                break;
            case string cadena:
                EscribirCadena(json, cadena);
                break;
            case int entero:
                json.Append(entero.ToString(CultureInfo.InvariantCulture));
                break;
            case long entero:
                json.Append(entero.ToString(CultureInfo.InvariantCulture));
                break;
            case IEnumerable<string> cadenas:
                json.Append('[');
                bool primero = true;
                foreach (string elemento in cadenas)
                {
                    if (!primero)
                    {
                        json.Append(',');
                    }

                    primero = false;
                    EscribirCadena(json, elemento);
                }

                json.Append(']');
                break;
            default:
                throw new ArgumentException(
                    $"El registro de auditoría solo admite cadenas, enteros, null y arreglos de cadenas; llegó {valor.GetType().Name}.",
                    nameof(valor));
        }
    }

    /// <summary>El escapado de JCS (el de <c>JSON.stringify</c>): solo comillas, barra invertida y caracteres de control.</summary>
    private static void EscribirCadena(StringBuilder json, string cadena)
    {
        json.Append('"');

        for (int i = 0; i < cadena.Length; i++)
        {
            char c = cadena[i];

            if (char.IsHighSurrogate(c) && i + 1 < cadena.Length && char.IsLowSurrogate(cadena[i + 1]))
            {
                json.Append(c).Append(cadena[++i]);
                continue;
            }

            if (char.IsSurrogate(c))
            {
                throw new ArgumentException("El registro de auditoría contiene un surrogate UTF-16 sin pareja.", nameof(cadena));
            }

            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\b': json.Append("\\b"); break;
                case '\f': json.Append("\\f"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                case < ' ':
                    json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    json.Append(c);
                    break;
            }
        }

        json.Append('"');
    }
}
