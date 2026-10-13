using Halsa.Backend.Domain.Clinico;

namespace Halsa.Backend.Application.Clinico;

/// <summary>
/// Los tipos de registro que admiten los dos canales de carga de la SPEC 06, con su código del contrato. El
/// <c>REPORTE_INICIAL</c> lo escribe la radicación, y el dictamen PCL tiene su propio flujo (SPEC 08).
/// </summary>
public static class TiposConcepto
{
    public const string ConceptoMedico = "CONCEPTO_MEDICO";
    public const string Evolucion = "EVOLUCION";
    public const string ConceptoRehabilitacion = "CONCEPTO_REHABILITACION";

    public static readonly IReadOnlyList<string> Codigos = [ConceptoMedico, Evolucion, ConceptoRehabilitacion];

    public static bool TryParse(string? codigo, out TipoRegistroClinico tipo)
    {
        (bool admitido, tipo) = codigo switch
        {
            ConceptoMedico => (true, TipoRegistroClinico.ConceptoMedico),
            Evolucion => (true, TipoRegistroClinico.Evolucion),
            ConceptoRehabilitacion => (true, TipoRegistroClinico.ConceptoRehabilitacion),
            _ => (false, default)
        };

        return admitido;
    }
}

/// <summary><c>Codificado</c> del contrato, restringido a CIE-10 en un diagnóstico.</summary>
public sealed record DiagnosticoRequest(string? Sistema, string? Codigo, string? Descripcion);

/// <summary>
/// <c>ContenidoClinico</c> del contrato: el texto clínico de un concepto, igual en los dos canales. La
/// descripción de un diagnóstico es para quien lee el JSON: la historia guarda el sistema y el código.
/// </summary>
public sealed record ContenidoClinicoRequest(
    string? Motivo,
    string? Anamnesis,
    string? ExamenFisico,
    string? Analisis,
    string? Plan,
    IReadOnlyList<DiagnosticoRequest>? Diagnosticos,
    string? Observaciones)
{
    /// <summary>Solo después de validar: sistema y código no son null.</summary>
    public ContenidoClinico ADominio() => new(
        Motivo, Anamnesis, ExamenFisico, Analisis, Plan,
        [.. (Diagnosticos ?? []).Select(d => new CodigoClinico(d.Sistema!, d.Codigo!))],
        Observaciones);
}

/// <summary>
/// <c>RegistroClinicoRequest</c> del contrato: el cuerpo de <c>POST /v1/casos/{casoId}/registros-clinicos</c>.
/// No lleva el profesional: sale del token (<c>IProfesionalActual</c>).
/// </summary>
/// <param name="Tipo"><c>CONCEPTO_MEDICO</c>, <c>EVOLUCION</c> o <c>CONCEPTO_REHABILITACION</c>.</param>
/// <param name="CorrigeA">Si viene, el registro corrige a ese otro del mismo caso.</param>
public sealed record RegistroClinicoRequest(
    string? Tipo,
    DateTimeOffset? AtendidoEn,
    Guid? CorrigeA,
    ContenidoClinicoRequest? Contenido);
