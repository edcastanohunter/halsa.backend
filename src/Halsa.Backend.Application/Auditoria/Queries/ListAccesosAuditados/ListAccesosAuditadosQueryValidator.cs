using FluentValidation;
using Halsa.Backend.Application.Abstractions.Auditoria;

namespace Halsa.Backend.Application.Auditoria.Queries.ListAccesosAuditados;

internal sealed class ListAccesosAuditadosQueryValidator : AbstractValidator<ListAccesosAuditadosQuery>
{
    public ListAccesosAuditadosQueryValidator()
    {
        RuleFor(query => query.Desde)
            .NotEmpty().WithErrorCode("Auditoria.DesdeRequerido");

        RuleFor(query => query.Hasta)
            .NotEmpty().WithErrorCode("Auditoria.HastaRequerido");

        // The range rules only make sense once both ends are present.
        When(query => query.Desde != default && query.Hasta != default, () =>
        {
            RuleFor(query => query.Hasta)
                .GreaterThan(query => query.Desde).WithErrorCode("Auditoria.RangoInvalido");

            RuleFor(query => query)
                .Must(query => query.Hasta - query.Desde <= TimeSpan.FromDays(ListAccesosAuditadosQuery.RangoMaximoDias))
                .WithErrorCode("Auditoria.RangoExcedido")
                .WithMessage($"El rango entre desde y hasta no puede superar {ListAccesosAuditadosQuery.RangoMaximoDias} días.");
        });

        RuleFor(query => query.Accion)
            .Must(accion => AuditoriaCodigos.TryParse<AccionAuditoria>(accion, out _))
            .WithErrorCode("Auditoria.AccionInvalida")
            .When(query => query.Accion is not null);

        RuleFor(query => query.Resultado)
            .Must(resultado => AuditoriaCodigos.TryParse<ResultadoAuditoria>(resultado, out _))
            .WithErrorCode("Auditoria.ResultadoInvalido")
            .When(query => query.Resultado is not null);

        RuleFor(query => query.ActorId)
            .MaximumLength(64).WithErrorCode("Auditoria.ActorIdDemasiadoLargo");

        RuleFor(query => query.Page)
            .GreaterThanOrEqualTo(1).WithErrorCode("Auditoria.PageInvalida");

        RuleFor(query => query.PageSize)
            .InclusiveBetween(1, ListAccesosAuditadosQuery.PageSizeMaximo).WithErrorCode("Auditoria.PageSizeInvalido");
    }
}
