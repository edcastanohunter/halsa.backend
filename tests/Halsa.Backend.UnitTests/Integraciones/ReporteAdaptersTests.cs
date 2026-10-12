using System.Text.Json.Nodes;
using Halsa.Backend.Application;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Application.Integraciones.Canonico;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Halsa.Backend.UnitTests.Integraciones;

/// <summary>SPEC 05, paso 5: los adaptadores FURAT/FUREP 1.0 y sus validadores, con el ejemplo del contrato.</summary>
public sealed class ReporteAdaptersTests
{
    private const string AutorSub = "01999000-0000-7000-8000-0000000a0001";

    private static readonly DateTimeOffset Ahora = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

    // The adapters and validators are internal: resolve them the way the radication does.
    private static readonly IReporteAdapter[] Adapters = [.. new ServiceCollection()
        .AddLogging()
        .AddSingleton<TimeProvider>(new FakeTimeProvider(Ahora))
        .AddApplication()
        .BuildServiceProvider()
        .CreateScope().ServiceProvider
        .GetServices<IReporteAdapter>()];

    private static Task<Result<ReporteCanonico>> AdaptarAsync(TipoReporte tipo, JsonObject cuerpo) =>
        Adapters.Single(a => a.Tipo == tipo).AdaptarAsync(cuerpo, AutorSub, CancellationToken.None);

    private static async Task<ReporteCanonico> AdaptarValidoAsync(TipoReporte tipo, JsonObject cuerpo)
    {
        Result<ReporteCanonico> result = await AdaptarAsync(tipo, cuerpo);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? Rutas(result.Error) : null);
        return result.Value;
    }

    private static async Task<Dictionary<string, string>> ErroresAsync(TipoReporte tipo, JsonObject cuerpo)
    {
        Result<ReporteCanonico> result = await AdaptarAsync(tipo, cuerpo);
        result.IsFailure.ShouldBeTrue();

        return result.Error.ShouldBeOfType<ValidationError>().Errors
            .Cast<ErrorDeCampo>()
            .GroupBy(e => e.Ruta)
            .ToDictionary(g => g.Key, g => g.First().Description);
    }

    private static string Rutas(Error error) => error is ValidationError v
        ? string.Join("; ", v.Errors.OfType<ErrorDeCampo>().Select(e => $"{e.Ruta}: {e.Description}"))
        : error.ToString();

    // ── FURAT ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Furat_Example_ShouldLeaveTheClinicalFieldsOutOfTheAdministrativeData()
    {
        ReporteCanonico reporte = await AdaptarValidoAsync(TipoReporte.Furat, EjemplosContrato.Objeto(EjemplosContrato.FuratCaidaAltura));

        JsonObject evento = reporte.DatosAdministrativos["evento"]!.AsObject();
        evento.ContainsKey("descripcion").ShouldBeFalse();
        evento.ContainsKey("tiposLesion").ShouldBeFalse();
        evento.ContainsKey("partesCuerpo").ShouldBeFalse();

        // The rest of the report stays, administrative fields of the event included.
        evento["agente"]!["codigo"]!.GetValue<string>().ShouldBe("300");
        reporte.DatosAdministrativos["trabajador"]!["primerNombre"]!.GetValue<string>().ShouldBe("Juan");
        reporte.DatosAdministrativos.ToJsonString().ShouldNotContain("segundo nivel del andamio");
    }

    [Fact]
    public async Task Furat_Example_ShouldMapTheCanonicalReport()
    {
        ReporteCanonico reporte = await AdaptarValidoAsync(TipoReporte.Furat, EjemplosContrato.Objeto(EjemplosContrato.FuratCaidaAltura));

        reporte.Tipo.ShouldBe(TipoReporte.Furat);
        reporte.TipoEvento.ShouldBe(TipoEvento.AccidenteTrabajo);
        reporte.NumeroReporteOrigen.ShouldBe("POS-FURAT-2026-884123");
        reporte.Trabajador.ShouldBe(new Identificacion("CC", "1020304050"));
        reporte.Empleador.ShouldBe(new Identificacion("NIT", "900123456"));
        reporte.FechaEvento.ShouldBe(new DateTimeOffset(2026, 10, 7, 14, 30, 0, TimeSpan.FromHours(-5)));
        reporte.MunicipioDivipola.ShouldBe("05001");
        reporte.EsGraveOMortal.ShouldBeTrue();

        reporte.Clinica.Relato!.ShouldStartWith("El trabajador cae desde el segundo nivel del andamio");
        reporte.Clinica.Codigos.ShouldBe(
        [
            new CodigoClinico("RES156-TIPO-LESION", "20"),
            new CodigoClinico("RES156-PARTE-CUERPO", "50")
        ]);
        reporte.Clinica.Autor.ShouldBe(new ProfesionalRef(AutorSub, "CC", "43123456", RegistroRethus: null));
    }

    [Fact]
    public async Task Furat_ClinicalFieldsWithAnotherCasing_ShouldStillBeRemoved()
    {
        JsonObject cuerpo = EjemplosContrato.Objeto(EjemplosContrato.FuratCaidaAltura);
        JsonObject evento = cuerpo["evento"]!.AsObject();
        evento.Remove("descripcion");
        evento["Descripcion"] = "Relato con otra capitalización, que también es clínico.";

        ReporteCanonico reporte = await AdaptarValidoAsync(TipoReporte.Furat, cuerpo);

        reporte.Clinica.Relato.ShouldBe("Relato con otra capitalización, que también es clínico.");
        reporte.DatosAdministrativos.ToJsonString().ShouldNotContain("otra capitalización");
    }

    [Fact]
    public async Task Furat_WithoutEsGrave_ButMortal_ShouldBeGraveOMortal()
    {
        JsonObject cuerpo = EjemplosContrato.Objeto(EjemplosContrato.FuratCaidaAltura);
        cuerpo["evento"]!.AsObject().Remove("esGrave");
        cuerpo["evento"]!["causoMuerte"] = true;

        (await AdaptarValidoAsync(TipoReporte.Furat, cuerpo)).EsGraveOMortal.ShouldBeTrue();
    }

    [Fact]
    public async Task Furat_WithAFutureEvent_ShouldFailOnEventoFechaHora()
    {
        JsonObject cuerpo = EjemplosContrato.Objeto(EjemplosContrato.FuratCaidaAltura);
        cuerpo["evento"]!["fechaHora"] = Ahora.AddHours(1).ToString("O");

        Dictionary<string, string> errores = await ErroresAsync(TipoReporte.Furat, cuerpo);

        errores.Keys.ShouldBe(["evento.fechaHora"]);
        errores["evento.fechaHora"].ShouldBe("La fecha del evento no puede ser futura.");
    }

    [Fact]
    public async Task Furat_WithMalformedFields_ShouldReportEachJsonPath()
    {
        JsonObject cuerpo = EjemplosContrato.Objeto(EjemplosContrato.FuratCaidaAltura);
        cuerpo["trabajador"]!["identificacion"]!["numero"] = "12-34";
        cuerpo["evento"]!["tiposLesion"]![0]!["sistema"] = "OTRO";
        cuerpo["evento"]!["descripcion"] = "Corto";
        cuerpo["empleador"]!["sede"]!["ubicacion"]!["municipio"]!["codigo"] = "5001";
        cuerpo.Remove("informante");

        Dictionary<string, string> errores = await ErroresAsync(TipoReporte.Furat, cuerpo);

        errores.Keys.ShouldBe(
        [
            "trabajador.identificacion.numero",
            "evento.tiposLesion[0].sistema",
            "evento.descripcion",
            "empleador.sede.ubicacion.municipio.codigo",
            "informante"
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task Furat_WithAFieldOfTheWrongType_ShouldFailOnItsPath()
    {
        JsonObject cuerpo = EjemplosContrato.Objeto(EjemplosContrato.FuratCaidaAltura);
        cuerpo["evento"]!["fechaHora"] = "ayer por la tarde";

        (await ErroresAsync(TipoReporte.Furat, cuerpo)).Keys.ShouldBe(["evento.fechaHora"]);
    }

    [Fact]
    public async Task Furat_WithAnotherVersion_ShouldFailOnVersion()
    {
        JsonObject cuerpo = EjemplosContrato.Objeto(EjemplosContrato.FuratCaidaAltura);
        cuerpo["version"] = "2.0";

        (await ErroresAsync(TipoReporte.Furat, cuerpo)).Keys.ShouldBe(["version"]);
    }

    // ── FUREP ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Furep_ShouldLeaveTheDiagnosesOutOfTheAdministrativeData()
    {
        ReporteCanonico reporte = await AdaptarValidoAsync(TipoReporte.Furep, EjemplosContrato.Objeto(EjemplosContrato.FurepAsma));

        JsonObject enfermedad = reporte.DatosAdministrativos["enfermedad"]!.AsObject();
        enfermedad.ContainsKey("diagnosticos").ShouldBeFalse();
        enfermedad.ContainsKey("factoresRiesgo").ShouldBeTrue();
        reporte.DatosAdministrativos.ToJsonString().ShouldNotContain("J45.0");
    }

    [Fact]
    public async Task Furep_ShouldMapTheCanonicalReport()
    {
        ReporteCanonico reporte = await AdaptarValidoAsync(TipoReporte.Furep, EjemplosContrato.Objeto(EjemplosContrato.FurepAsma));

        reporte.TipoEvento.ShouldBe(TipoEvento.EnfermedadLaboral);
        reporte.FechaEvento.ShouldBe(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(-5)));
        reporte.EsGraveOMortal.ShouldBeFalse();
        reporte.MunicipioDivipola.ShouldBe("11001");
        reporte.Clinica.Relato.ShouldBeNull();
        reporte.Clinica.Codigos.ShouldBe([new CodigoClinico("CIE-10", "J45.0")]);
        reporte.Clinica.Autor.ShouldBe(new ProfesionalRef(AutorSub, "CC", "80123123", "RM-55667"));
    }

    [Fact]
    public async Task Furep_WithoutDiagnoses_ShouldFailOnEnfermedadDiagnosticos()
    {
        JsonObject cuerpo = EjemplosContrato.Objeto(EjemplosContrato.FurepAsma);
        cuerpo["enfermedad"]!["diagnosticos"] = new JsonArray();

        (await ErroresAsync(TipoReporte.Furep, cuerpo)).Keys.ShouldBe(["enfermedad.diagnosticos"]);
    }

    [Fact]
    public async Task Furep_WithoutRethus_ShouldFailOnTheDiagnosingDoctor()
    {
        JsonObject cuerpo = EjemplosContrato.Objeto(EjemplosContrato.FurepAsma);
        cuerpo["enfermedad"]!["medicoDiagnostica"]!.AsObject().Remove("registroRethus");

        (await ErroresAsync(TipoReporte.Furep, cuerpo)).Keys.ShouldBe(["enfermedad.medicoDiagnostica.registroRethus"]);
    }
}
