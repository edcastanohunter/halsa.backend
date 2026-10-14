using System.Text.Json;
using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Domain.Dictamenes;

namespace Halsa.Backend.Application.Dictamenes;

/// <summary>
/// El cuerpo de <c>POST /v1/casos/{casoId}/dictamen-pcl</c> (SPEC 08). Todo anulable para que el validador diga qué
/// falta; el calificador sale del token, nunca del cuerpo.
/// </summary>
public sealed record RegistrarDictamenPclRequest(
    decimal? PorcentajePcl,
    string? Origen,
    DateOnly? FechaEstructuracion,
    DateOnly? FechaDictamen,
    ComponentesPcl? Componentes,
    string? Fundamentos,
    IReadOnlyList<DiagnosticoRequest>? Diagnosticos,
    Guid? CorrigeA);

/// <summary>Los dos componentes del Decreto 1507/2014. Si vienen, suman el porcentaje (±0,01).</summary>
public sealed record ComponentesPcl(decimal? Deficiencias, decimal? RolLaboralYOtrasAreas);

/// <summary>
/// <c>DictamenPclResponse</c> del contrato: la parte administrativa más la clínica descifrada. <see cref="Id"/> es el que
/// se indica en <c>corrigeA</c> para corregirlo.
/// </summary>
public sealed record DictamenPclResponse(
    Guid Id,
    Guid CasoId,
    decimal PorcentajePcl,
    string Origen,
    DateOnly FechaEstructuracion,
    DateOnly FechaDictamen,
    string Manual,
    ComponentesPcl? Componentes,
    string Fundamentos,
    IReadOnlyList<CodigoClinico> Diagnosticos,
    CalificadorResponse Calificador,
    string EstadoNotificacion,
    DateOnly? VenceControversiaEn,
    Guid? CorrigeA);

/// <summary><c>Profesional</c> del contrato.</summary>
public sealed record CalificadorResponse(IdentificacionResponse Identificacion, string? NombreCompleto, string RegistroRethus);

public sealed record IdentificacionResponse(string Tipo, string Numero);

/// <summary>Cómo se arma la parte clínica del dictamen en un registro <c>DICTAMEN_PCL</c> y cómo se lee de vuelta.</summary>
public static class DictamenPclClinico
{
    /// <summary>El manual de calificación vigente.</summary>
    public const string Manual = "Decreto 1507/2014";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary><c>Analisis</c> = fundamentos, <c>Diagnosticos</c> = diagnósticos, <c>Observaciones</c> = JSON de los componentes.</summary>
    public static ContenidoClinico Contenido(string fundamentos, IReadOnlyList<CodigoClinico> diagnosticos, ComponentesPcl? componentes) =>
        new(
            Motivo: null,
            Anamnesis: null,
            ExamenFisico: null,
            Analisis: fundamentos,
            Plan: null,
            diagnosticos,
            componentes is null ? null : JsonSerializer.Serialize(componentes, Json));

    public static ComponentesPcl? Componentes(ContenidoClinico contenido)
    {
        ArgumentNullException.ThrowIfNull(contenido);

        return contenido.Observaciones is null ? null : JsonSerializer.Deserialize<ComponentesPcl>(contenido.Observaciones, Json);
    }

    /// <param name="venceControversiaEn">La fecha (en Colombia) en que vence su <c>CONTROVERSIA_DICTAMEN</c>, si ya se abrió.</param>
    public static DictamenPclResponse Respuesta(
        DictamenPcl dictamen,
        ProfesionalRef firma,
        ContenidoClinico contenido,
        DateOnly? venceControversiaEn = null)
    {
        ArgumentNullException.ThrowIfNull(dictamen);
        ArgumentNullException.ThrowIfNull(firma);
        ArgumentNullException.ThrowIfNull(contenido);

        return new DictamenPclResponse(
            dictamen.Id.Value,
            dictamen.CasoMedicoId.Value,
            dictamen.PorcentajePcl,
            CodigosDictamen.Codigo(dictamen.Origen),
            dictamen.FechaEstructuracion,
            dictamen.FechaDictamen,
            Manual,
            Componentes(contenido),
            contenido.Analisis ?? string.Empty,
            contenido.Diagnosticos,
            new CalificadorResponse(
                new IdentificacionResponse(firma.TipoDocumento, firma.NumeroDocumento),
                dictamen.CalificadorNombre,
                dictamen.CalificadorRegistroRethus),
            CodigosDictamen.Codigo(dictamen.EstadoNotificacion),
            venceControversiaEn,
            dictamen.CorrigeA?.Value);
    }
}
