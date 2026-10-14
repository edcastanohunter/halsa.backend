using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Domain.Incapacidades;

/// <summary>
/// Las reglas de la SPEC 07, que responden <c>422</c> con el código de la tabla de la spec tal cual
/// (<c>INCAPACIDAD_SOLAPADA</c>, …). Están pendientes de validación jurídica antes de producción.
/// </summary>
public static class IncapacidadErrors
{
    public static readonly Error CasoCerrado =
        Error.Unprocessable("CASO_CERRADO",
            "El caso está cerrado o rechazado y no admite incapacidades.");

    public static readonly Error Solapada =
        Error.Unprocessable("INCAPACIDAD_SOLAPADA",
            "El periodo de la incapacidad se cruza con otra incapacidad del mismo caso.");

    public static readonly Error ProrrogaSinInicial =
        Error.Unprocessable("PRORROGA_SIN_INICIAL",
            "Una prórroga debe indicar en prorrogaDe una incapacidad existente del mismo caso.");

    public static readonly Error ProrrogaNoEsUltima =
        Error.Unprocessable("PRORROGA_NO_ES_ULTIMA",
            "La incapacidad que se prorroga no es la última de su cadena.");

    public static readonly Error ProrrogaDiscontinua =
        Error.Unprocessable("PRORROGA_DISCONTINUA",
            "Entre el fin de la incapacidad anterior y el inicio de la prórroga hay más días de los permitidos.");

    public static readonly Error ProrrogaDiagnosticoNoRelacionado =
        Error.Unprocessable("PRORROGA_DIAGNOSTICO_NO_RELACIONADO",
            "La categoría CIE-10 del diagnóstico de la prórroga no es la de la incapacidad inicial de la cadena.");

    public static readonly Error FechaAnteriorAlEvento =
        Error.Unprocessable("FECHA_ANTERIOR_AL_EVENTO",
            "La fecha de inicio de la incapacidad es anterior a la fecha del evento del caso.");

    /// <summary>Una guarda: el validador del request ya lo impide.</summary>
    public static readonly Error DiasFueraDeRango =
        Error.Problem("Incapacidades.DiasFueraDeRango",
            $"Una incapacidad dura entre 1 y {Incapacidad.MaxDias} días.");

    /// <summary>Una guarda: el validador del request ya lo impide.</summary>
    public static readonly Error InicialConProrrogaDe =
        Error.Problem("Incapacidades.InicialConProrrogaDe",
            "Una incapacidad inicial no prorroga a otra.");
}
