using System.Text.Json.Nodes;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;

namespace Halsa.Backend.Application.Integraciones.Canonico;

/// <summary>
/// Un FURAT o un FUREP, ya validado, en la forma que entiende la radicación. Lo construyen los adaptadores
/// de cada formato (<c>FuratV1Adapter</c>, <c>FurepV1Adapter</c>); la radicación no conoce el formato de origen.
/// </summary>
/// <param name="FechaEvento">FURAT: <c>evento.fechaHora</c>. FUREP: <c>fechaDiagnostico</c> a las 00:00 -05:00.</param>
/// <param name="EsGraveOMortal">FURAT: <c>esGrave || causoMuerte</c>. FUREP: siempre <c>false</c>.</param>
/// <param name="DatosAdministrativos">El cuerpo recibido sin los campos <c>x-clasificacion: clinico</c>.</param>
public sealed record ReporteCanonico(
    TipoReporte Tipo,
    string NumeroReporteOrigen,
    Identificacion Trabajador,
    Identificacion Empleador,
    DateTimeOffset FechaEvento,
    string MunicipioDivipola,
    bool EsGraveOMortal,
    JsonObject DatosAdministrativos,
    ParteClinica Clinica)
{
    public TipoEvento TipoEvento => Tipo == TipoReporte.Furat ? TipoEvento.AccidenteTrabajo : TipoEvento.EnfermedadLaboral;
}

/// <summary>Lo clínico del reporte: va cifrado al <c>REPORTE_INICIAL</c> de la historia clínica.</summary>
/// <param name="Relato">FURAT: <c>evento.descripcion</c>. FUREP: no tiene.</param>
/// <param name="Codigos">FURAT: <c>tiposLesion</c> y <c>partesCuerpo</c>. FUREP: <c>diagnosticos</c> (CIE-10).</param>
/// <param name="Autor">FURAT: el informante, sin RETHUS. FUREP: <c>medicoDiagnostica</c>, con RETHUS.</param>
public sealed record ParteClinica(
    string? Relato,
    IReadOnlyList<CodigoClinico> Codigos,
    ProfesionalRef Autor);
