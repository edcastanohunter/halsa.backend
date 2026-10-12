using FluentValidation;
using FluentValidation.Results;
using Halsa.Backend.Application;
using Halsa.Backend.Application.Auditoria.Queries.ListAccesosAuditados;
using Microsoft.Extensions.DependencyInjection;

namespace Halsa.Backend.UnitTests.Auditoria;

public sealed class ListAccesosAuditadosQueryValidatorTests
{
    private static readonly DateTimeOffset Desde = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    // The validator is internal; resolve it the way the pipeline does.
    private static readonly IValidator<ListAccesosAuditadosQuery> Validator = new ServiceCollection()
        .AddApplication()
        .BuildServiceProvider()
        .GetRequiredService<IValidator<ListAccesosAuditadosQuery>>();

    private static string[] Codes(ListAccesosAuditadosQuery query)
    {
        ValidationResult result = Validator.Validate(query);
        return [.. result.Errors.Select(e => e.ErrorCode)];
    }

    [Fact]
    public void Validator_ShouldAcceptARangeOf31Days()
    {
        Codes(new ListAccesosAuditadosQuery(Desde, Desde.AddDays(31))).ShouldBeEmpty();
    }

    [Fact]
    public void Validator_ShouldRejectARangeOf32Days()
    {
        Codes(new ListAccesosAuditadosQuery(Desde, Desde.AddDays(32))).ShouldContain("Auditoria.RangoExcedido");
    }

    [Fact]
    public void Validator_ShouldRequireBothDates()
    {
        Codes(new ListAccesosAuditadosQuery(default, default))
            .ShouldBe(["Auditoria.DesdeRequerido", "Auditoria.HastaRequerido"], ignoreOrder: true);
    }

    [Fact]
    public void Validator_ShouldRejectHastaBeforeDesde()
    {
        Codes(new ListAccesosAuditadosQuery(Desde, Desde.AddDays(-1))).ShouldContain("Auditoria.RangoInvalido");
    }

    [Fact]
    public void Validator_ShouldRejectUnknownAccionAndResultado()
    {
        Codes(new ListAccesosAuditadosQuery(Desde, Desde.AddDays(1), Accion: "BORRAR", Resultado: "QUIZAS"))
            .ShouldBe(["Auditoria.AccionInvalida", "Auditoria.ResultadoInvalido"], ignoreOrder: true);
    }
}
