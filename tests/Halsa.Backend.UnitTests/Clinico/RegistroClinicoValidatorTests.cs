using FluentValidation;
using FluentValidation.Results;
using Halsa.Backend.Application;
using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Application.Clinico.Commands.Registrar;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Microsoft.Extensions.DependencyInjection;

namespace Halsa.Backend.UnitTests.Clinico;

public sealed class RegistroClinicoValidatorTests
{
    // The validators are internal: resolve them the way the pipeline does.
    private static readonly IValidator<RegistrarRegistroClinicoCommand> Validator = new ServiceCollection()
        .AddLogging()
        .AddApplication()
        .BuildServiceProvider()
        .GetRequiredService<IValidator<RegistrarRegistroClinicoCommand>>();

    private static readonly DatosProfesional Medico = new("CC", "12345678", "RM-12345");

    private static readonly ContenidoClinico ConTexto =
        new("Dolor", null, null, null, null, [new CodigoClinico("CIE-10", "S62.6")], null);

    private static ValidationResult Validar(
        TipoRegistroClinico tipo = TipoRegistroClinico.ConceptoMedico,
        ContenidoClinico? contenido = null,
        DatosProfesional? profesional = null) =>
        Validator.Validate(new RegistrarRegistroClinicoCommand(
            CasoMedicoId.New(), tipo, DateTimeOffset.UtcNow.AddHours(-1), contenido ?? ConTexto, profesional ?? Medico));

    [Fact]
    public void Validator_ShouldAcceptAValidCommand()
    {
        Validar().IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validator_ShouldRejectAContenidoWithoutText()
    {
        ValidationResult result = Validar(contenido: new ContenidoClinico(" ", "", null, null, null, [], null));

        result.Errors.ShouldContain(e => e.ErrorCode == "Clinico.ContenidoVacio");
    }

    [Fact]
    public void Validator_ShouldRejectADiagnosisWithoutCode()
    {
        ValidationResult result = Validar(contenido: ConTexto with { Diagnosticos = [new CodigoClinico("CIE-10", "")] });

        result.Errors.ShouldContain(e => e.ErrorCode == "Clinico.DiagnosticoCodigoRequerido");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Validator_ShouldRequireRethus_OutsideReporteInicial(string? rethus)
    {
        ValidationResult result = Validar(profesional: Medico with { RegistroRethus = rethus });

        result.Errors.ShouldContain(e => e.ErrorCode == "Clinico.RethusRequerido");
    }

    [Fact]
    public void Validator_ShouldNotRequireRethus_InReporteInicial()
    {
        Validar(TipoRegistroClinico.ReporteInicial, profesional: Medico with { RegistroRethus = null })
            .IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validator_ShouldRequireTheProfessionalsDocument()
    {
        ValidationResult result = Validar(profesional: Medico with { NumeroDocumento = "" });

        result.Errors.ShouldContain(e => e.ErrorCode == "Clinico.ProfesionalNumeroDocumentoRequerido");
    }
}
