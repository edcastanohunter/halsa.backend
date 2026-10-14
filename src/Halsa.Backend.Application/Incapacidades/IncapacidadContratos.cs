using Halsa.Backend.Application.Integraciones.Contratos;
using Halsa.Backend.Domain.Incapacidades;

namespace Halsa.Backend.Application.Incapacidades;

/// <summary>
/// <c>IncapacidadRequest</c> del contrato: la parte <c>datos</c> del multipart. <see cref="Profesional"/> es obligatorio
/// solo en la integración; en el backoffice el profesional sale del token y este campo se ignora.
/// </summary>
/// <param name="ProrrogaDe">Obligatorio si <c>tipo=PRORROGA</c>: la incapacidad anterior del mismo caso.</param>
/// <param name="DiagnosticoPrincipal">CIE-10. Se cifra en la historia clínica y nunca se devuelve.</param>
/// <param name="DiagnosticosRelacionados">Hasta 3, CIE-10.</param>
/// <param name="Observaciones">Clínico: se cifra con el diagnóstico.</param>
public sealed record IncapacidadRequest(
    string? NumeroCertificado,
    string? Tipo,
    Guid? ProrrogaDe,
    DateOnly? FechaInicio,
    int? Dias,
    CodificadoV1? DiagnosticoPrincipal,
    IReadOnlyList<CodificadoV1>? DiagnosticosRelacionados,
    ProfesionalV1? Profesional,
    IpsEmisoraV1? IpsEmisora,
    string? Modalidad,
    string? Observaciones);

public sealed record IpsEmisoraV1(string? CodigoReps, string? Nombre);

/// <summary>
/// <c>IncapacidadResponse</c> del contrato: solo datos administrativos y alertas. Ni diagnósticos, ni observaciones,
/// ni el profesional.
/// </summary>
public sealed record IncapacidadResponse(
    Guid Id,
    Guid CasoId,
    string Tipo,
    DateOnly FechaInicio,
    DateOnly FechaFin,
    int Dias,
    int DiasAcumulados,
    string Estado,
    Guid SoporteId,
    IReadOnlyList<AlertaResponse> Alertas)
{
    public static IncapacidadResponse Desde(Incapacidad incapacidad, ParametrosIncapacidad parametros)
    {
        ArgumentNullException.ThrowIfNull(incapacidad);

        return new IncapacidadResponse(
            incapacidad.Id.Value,
            incapacidad.CasoMedicoId.Value,
            CodigosIncapacidad.Codigo(incapacidad.Tipo),
            incapacidad.FechaInicio,
            incapacidad.FechaFin,
            incapacidad.Dias,
            incapacidad.DiasAcumulados,
            CodigosIncapacidad.Codigo(incapacidad.Estado),
            incapacidad.SoporteId,
            [.. incapacidad.Alertas(parametros).Select(code => AlertaResponse.Para(code, parametros))]);
    }
}

/// <summary>Una alerta de la respuesta: no bloquea la carga.</summary>
public sealed record AlertaResponse(string Code, string Mensaje)
{
    public static AlertaResponse Para(string code, ParametrosIncapacidad parametros)
    {
        ArgumentNullException.ThrowIfNull(parametros);

        return new AlertaResponse(code, code switch
        {
            AlertasIncapacidad.Acumulado150Dias => "La cadena de incapacidades acumula 150 días o más.",
            AlertasIncapacidad.Acumulado180Dias =>
                "La cadena acumula 180 días o más: una prórroga requiere concepto de rehabilitación.",
            AlertasIncapacidad.Acumulado540Dias => "La cadena de incapacidades acumula 540 días o más.",
            AlertasIncapacidad.CargaExtemporanea =>
                $"El certificado se cargó más de {parametros.DiasCargaExtemporanea} días después de su inicio.",
            _ => code
        });
    }
}

/// <summary>Los códigos del contrato para los enums de la incapacidad.</summary>
public static class CodigosIncapacidad
{
    public const string Inicial = "INICIAL";
    public const string Prorroga = "PRORROGA";
    public const string Presencial = "PRESENCIAL";
    public const string Telemedicina = "TELEMEDICINA";

    public static readonly IReadOnlyList<string> Tipos = [Inicial, Prorroga];

    public static readonly IReadOnlyList<string> Modalidades = [Presencial, Telemedicina];

    public static string Codigo<TEnum>(TEnum valor) where TEnum : struct, Enum => valor.ToString().ToUpperInvariant();

    public static bool TryParse<TEnum>(string? codigo, out TEnum valor) where TEnum : struct, Enum
    {
        // Only the contract's exact spelling: "Inicial" or "1" are not codes.
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
}
