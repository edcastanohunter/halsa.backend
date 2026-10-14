using System.Text;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Domain.Dictamenes;

public readonly record struct DictamenPclId(Guid Value)
{
    public static DictamenPclId New() => new(Guid.CreateVersion7());
}

/// <summary>El origen calificado. Se guarda como <c>LABORAL</c>, <c>COMUN</c> o <c>MIXTO</c>.</summary>
public enum OrigenPcl
{
    Laboral,
    Comun,
    Mixto
}

/// <summary>
/// Dónde está la notificación del dictamen. <see cref="EnFirme"/> llega con la SPEC 09, al vencer la controversia;
/// <see cref="EnControversia"/>, con el registro de una controversia (fuera de ARQ 01).
/// Se guarda como <c>PENDIENTE</c>, <c>NOTIFICADO</c>, <c>EN_CONTROVERSIA</c> o <c>EN_FIRME</c>.
/// </summary>
public enum EstadoNotificacionDictamen
{
    Pendiente,
    Notificado,
    EnControversia,
    EnFirme
}

/// <summary>Cómo se notificó. Se guarda como <c>CORREO_CERTIFICADO</c>, <c>ELECTRONICO</c> o <c>PERSONAL</c>.</summary>
public enum MedioNotificacion
{
    CorreoCertificado,
    Electronico,
    Personal
}

/// <summary>
/// Los códigos del contrato y de la base para los enums del dictamen: el nombre en mayúsculas, con <c>_</c> entre
/// palabras (<see cref="EstadoNotificacionDictamen.EnControversia"/> → <c>EN_CONTROVERSIA</c>).
/// </summary>
public static class CodigosDictamen
{
    public static string Codigo<TEnum>(TEnum valor) where TEnum : struct, Enum
    {
        string nombre = valor.ToString();
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

    /// <summary>Solo la grafía exacta del contrato: <c>Laboral</c> o <c>0</c> no son códigos.</summary>
    public static bool TryParse<TEnum>(string? codigo, out TEnum valor) where TEnum : struct, Enum
    {
        foreach (TEnum candidato in Enum.GetValues<TEnum>())
        {
            if (string.Equals(codigo, Codigo(candidato), StringComparison.Ordinal))
            {
                valor = candidato;
                return true;
            }
        }

        valor = default;
        return false;
    }

    public static TEnum Parse<TEnum>(string codigo) where TEnum : struct, Enum =>
        TryParse(codigo, out TEnum valor)
            ? valor
            : throw new FormatException($"'{codigo}' no es un código de {typeof(TEnum).Name}.");
}

/// <summary>Errores del dictamen PCL. Todos tienen código <c>Dictamen.*</c>.</summary>
public static class DictamenErrors
{
    public static readonly Error YaExiste =
        Error.Conflict("Dictamen.YaExiste",
            "El caso ya tiene un dictamen vigente. Para corregirlo, indique su id en corrigeA.");

    public static readonly Error YaNotificado =
        Error.Conflict("Dictamen.YaNotificado",
            "El dictamen ya se notificó y no admite correcciones.");

    public static readonly Error CorrigeANoVigente =
        Error.Conflict("Dictamen.CorrigeANoVigente",
            "corrigeA no es el dictamen vigente del caso.");

    public static readonly Error NoEncontrado =
        Error.NotFound("Dictamen.NoEncontrado",
            "El caso no tiene un dictamen vigente.");

    public static readonly Error ActorNoAutorizado =
        Error.Forbidden("Dictamen.ActorNoAutorizado",
            "Solo el personal de la ARL del caso registra dictámenes PCL.");

    public static readonly Error PorcentajeFueraDeRango =
        Error.Problem("Dictamen.PorcentajeFueraDeRango",
            "El porcentaje de PCL va de 0 a 100, con dos decimales como máximo.");

    public static readonly Error NotificacionFueraDeRango =
        Error.Problem("Dictamen.NotificacionFueraDeRango",
            "La notificación no puede ser futura ni anterior a la fecha del dictamen.");

    public static readonly Error FechasInvalidas =
        Error.Problem("Dictamen.FechasInvalidas",
            "La fecha de estructuración no puede ser posterior a la del dictamen, ni la del dictamen futura.");
}
