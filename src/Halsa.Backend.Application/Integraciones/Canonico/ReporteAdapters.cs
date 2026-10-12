using System.Text.Json.Nodes;
using FluentValidation;
using FluentValidation.Results;
using Halsa.Backend.Application.Integraciones.Contratos;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Integraciones.Canonico;

/// <summary>
/// Lee un formato de reporte (FURAT 1.0, FUREP 1.0, mañana FHIR) y lo lleva al <see cref="ReporteCanonico"/>.
/// La radicación elige el adaptador por <see cref="Tipo"/> y nunca ve el formato de origen.
/// </summary>
public interface IReporteAdapter
{
    TipoReporte Tipo { get; }

    /// <summary>
    /// Deserializa y valida el formato. Si es válido, construye el reporte canónico; si no, devuelve una
    /// <see cref="ValidationError"/> de <see cref="ErrorDeCampo"/>s por ruta JSON.
    /// </summary>
    /// <param name="autorSub">El <c>sub</c> del actor que radica: firma el <c>REPORTE_INICIAL</c>.</param>
    Task<Result<ReporteCanonico>> AdaptarAsync(JsonObject cuerpo, string autorSub, CancellationToken ct);
}

/// <summary>FURAT 1.0 (Res. 156/2005): accidente de trabajo.</summary>
internal sealed class FuratV1Adapter(IValidator<FuratV1> validator) : IReporteAdapter
{
    /// <summary>Los campos <c>x-clasificacion: clinico</c> de <c>evento</c>.</summary>
    private static readonly string[] CamposClinicos = ["descripcion", "tiposLesion", "partesCuerpo"];

    public TipoReporte Tipo => TipoReporte.Furat;

    public async Task<Result<ReporteCanonico>> AdaptarAsync(JsonObject cuerpo, string autorSub, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);

        Result<FuratV1> leido = LecturaJson.Leer<FuratV1>(cuerpo);
        if (leido.IsFailure)
        {
            return Result.Failure<ReporteCanonico>(leido.Error);
        }

        ValidationResult validacion = await validator.ValidateAsync(leido.Value, ct);
        if (!validacion.IsValid)
        {
            return Result.Failure<ReporteCanonico>(LecturaJson.Errores(validacion));
        }

        FuratV1 furat = leido.Value;
        EventoV1 evento = furat.Evento!;
        InformanteV1 informante = furat.Informante!;

        IReadOnlyList<CodigoClinico> codigos =
        [
            .. (evento.TiposLesion ?? []).Select(Codigo),
            .. (evento.PartesCuerpo ?? []).Select(Codigo)
        ];

        // El informante de un FURAT no es médico: no tiene RETHUS (SPEC 03 lo admite solo en el reporte inicial).
        var autor = new ProfesionalRef(
            autorSub, informante.Identificacion!.Tipo!, informante.Identificacion.Numero!, RegistroRethus: null);

        return Result.Success(new ReporteCanonico(
            Tipo,
            furat.NumeroReporteOrigen!,
            Identificacion(furat.Trabajador!.Identificacion!),
            Identificacion(furat.Empleador!.Identificacion!),
            evento.FechaHora!.Value,
            evento.Ubicacion!.Municipio!.Codigo!,
            evento.EsGrave == true || evento.CausoMuerte == true,
            DatosAdministrativos.SinClinicos(cuerpo, "evento", CamposClinicos),
            new ParteClinica(evento.Descripcion, codigos, autor)));
    }

    internal static Identificacion Identificacion(IdentificacionV1 identificacion) =>
        new(identificacion.Tipo!, identificacion.Numero!);

    internal static CodigoClinico Codigo(CodificadoV1 codigo) => new(codigo.Sistema!, codigo.Codigo!);
}

/// <summary>FUREP 1.0 (Res. 156/2005): enfermedad laboral.</summary>
internal sealed class FurepV1Adapter(IValidator<FurepV1> validator) : IReporteAdapter
{
    /// <summary>Los campos <c>x-clasificacion: clinico</c> de <c>enfermedad</c>.</summary>
    private static readonly string[] CamposClinicos = ["diagnosticos"];

    public TipoReporte Tipo => TipoReporte.Furep;

    public async Task<Result<ReporteCanonico>> AdaptarAsync(JsonObject cuerpo, string autorSub, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cuerpo);

        Result<FurepV1> leido = LecturaJson.Leer<FurepV1>(cuerpo);
        if (leido.IsFailure)
        {
            return Result.Failure<ReporteCanonico>(leido.Error);
        }

        ValidationResult validacion = await validator.ValidateAsync(leido.Value, ct);
        if (!validacion.IsValid)
        {
            return Result.Failure<ReporteCanonico>(LecturaJson.Errores(validacion));
        }

        FurepV1 furep = leido.Value;
        EnfermedadV1 enfermedad = furep.Enfermedad!;
        ProfesionalV1 medico = enfermedad.MedicoDiagnostica!;

        var autor = new ProfesionalRef(
            autorSub, medico.Identificacion!.Tipo!, medico.Identificacion.Numero!, medico.RegistroRethus);

        // Una enfermedad no tiene hora: el evento es el día del diagnóstico a las 00:00 en Colombia, que no
        // tiene horario de verano, así que la zona -05:00 es fija.
        var fechaEvento = new DateTimeOffset(
            enfermedad.FechaDiagnostico!.Value.ToDateTime(TimeOnly.MinValue), ValoresV1.ZonaColombia);

        return Result.Success(new ReporteCanonico(
            Tipo,
            furep.NumeroReporteOrigen!,
            FuratV1Adapter.Identificacion(furep.Trabajador!.Identificacion!),
            FuratV1Adapter.Identificacion(furep.Empleador!.Identificacion!),
            fechaEvento,
            furep.Empleador!.Sede!.Ubicacion!.Municipio!.Codigo!,
            EsGraveOMortal: false,
            DatosAdministrativos.SinClinicos(cuerpo, "enfermedad", CamposClinicos),
            new ParteClinica(Relato: null, [.. enfermedad.Diagnosticos!.Select(FuratV1Adapter.Codigo)], autor)));
    }
}
