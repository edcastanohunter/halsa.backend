using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation.Results;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Integraciones.Contratos;

/// <summary>
/// Del cuerpo JSON a los contratos, con los errores por ruta JSON (<c>evento.fechaHora</c>) que pide el contrato.
/// </summary>
internal static class LecturaJson
{
    public const string RutaRaiz = "$";

    public static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web);

    /// <summary>El cuerpo crudo como objeto JSON. Cualquier otra cosa (un arreglo, JSON inválido) es un error en <c>$</c>.</summary>
    public static Result<JsonObject> Objeto(ReadOnlyMemory<byte> cuerpo)
    {
        try
        {
            if (JsonNode.Parse(cuerpo.Span) is JsonObject objeto)
            {
                return Result.Success(objeto);
            }
        }
        catch (JsonException)
        {
            // Falls through to the same error as a non-object body.
        }
        catch (ArgumentException)
        {
            // Duplicate property names.
        }

        return Result.Failure<JsonObject>(IntegracionErrors.Validacion(
            [new ErrorDeCampo(RutaRaiz, "El cuerpo debe ser un objeto JSON válido.")]));
    }

    /// <summary>El contrato. Un campo con un tipo inesperado (una fecha mal escrita) es un error en su ruta.</summary>
    public static Result<T> Leer<T>(JsonObject cuerpo)
        where T : class
    {
        try
        {
            T? contrato = cuerpo.Deserialize<T>(Opciones);
            if (contrato is not null)
            {
                return Result.Success(contrato);
            }
        }
        catch (JsonException ex)
        {
            return Result.Failure<T>(IntegracionErrors.Validacion(
                [new ErrorDeCampo(RutaDesdeJsonPath(ex.Path), "Tipo o formato inválido.")]));
        }

        return Result.Failure<T>(IntegracionErrors.Validacion(
            [new ErrorDeCampo(RutaRaiz, "El cuerpo debe ser un objeto JSON válido.")]));
    }

    public static ValidationError Errores(ValidationResult resultado) =>
        IntegracionErrors.Validacion(resultado.Errors.Select(fallo =>
            new ErrorDeCampo(RutaDesdePropiedad(fallo.PropertyName), fallo.ErrorMessage)));

    /// <summary><c>Evento.TiposLesion[0].Codigo</c> → <c>evento.tiposLesion[0].codigo</c>.</summary>
    public static string RutaDesdePropiedad(string propiedad)
    {
        if (string.IsNullOrEmpty(propiedad))
        {
            return RutaRaiz;
        }

        var ruta = new StringBuilder(propiedad.Length);
        bool inicioDeSegmento = true;

        foreach (char c in propiedad)
        {
            ruta.Append(inicioDeSegmento ? char.ToLowerInvariant(c) : c);
            inicioDeSegmento = c == '.';
        }

        return ruta.ToString();
    }

    /// <summary><c>$.evento.fechaHora</c> → <c>evento.fechaHora</c>.</summary>
    private static string RutaDesdeJsonPath(string? path) =>
        string.IsNullOrEmpty(path) || path == RutaRaiz
            ? RutaRaiz
            : path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path;
}

/// <summary>Separa lo administrativo de lo clínico en el cuerpo recibido.</summary>
internal static class DatosAdministrativos
{
    /// <summary>
    /// Una copia de <paramref name="cuerpo"/> sin las claves <paramref name="clinicas"/> dentro de
    /// <paramref name="objeto"/>. Sin distinguir mayúsculas: la deserialización tampoco las distingue, así que
    /// un <c>"Descripcion"</c> llegaría al relato clínico y no debe quedarse en los datos administrativos.
    /// </summary>
    public static JsonObject SinClinicos(JsonObject cuerpo, string objeto, params string[] clinicas)
    {
        var copia = cuerpo.DeepClone().AsObject();

        foreach (JsonObject contenedor in Claves(copia, objeto).Select(clave => copia[clave]).OfType<JsonObject>())
        {
            foreach (string clinica in clinicas)
            {
                foreach (string clave in Claves(contenedor, clinica))
                {
                    contenedor.Remove(clave);
                }
            }
        }

        return copia;
    }

    private static List<string> Claves(JsonObject objeto, string nombre) =>
        [.. objeto.Select(propiedad => propiedad.Key).Where(clave => string.Equals(clave, nombre, StringComparison.OrdinalIgnoreCase))];
}
