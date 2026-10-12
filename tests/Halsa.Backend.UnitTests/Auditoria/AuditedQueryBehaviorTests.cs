using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Behaviors;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;

namespace Halsa.Backend.UnitTests.Auditoria;

public sealed class AuditedQueryBehaviorTests
{
    private static readonly Guid CasoId = Guid.CreateVersion7();

    public sealed record SampleAuditedQuery : IQuery<string>, IAuditedQuery
    {
        public AccionAuditoria Accion => AccionAuditoria.Leer;

        public ObjetivoAuditoria Objetivo => new(RecursoAuditoria.Caso, CasoId.ToString(), CasoId);
    }

    private readonly IAuditWriter _writer = Substitute.For<IAuditWriter>();
    private readonly AuditScope _scope = new();

    private AuditedQueryBehavior<SampleAuditedQuery, Result<string>> CreateBehavior() =>
        new(_writer, _scope, NullLogger<AuditedQueryBehavior<SampleAuditedQuery, Result<string>>>.Instance);

    [Fact]
    public async Task Handle_ShouldDiscardTheData_WhenTheAuditWriterThrows()
    {
        _writer
            .RegistrarAsync(default, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("audit down")));

        Result<string> result = await CreateBehavior().Handle(
            new SampleAuditedQuery(),
            _ => Task.FromResult(Result.Success("RAD-SECRETO")),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(AuditoriaErrors.NoRegistrada);
        result.Error.Type.ShouldBe(ErrorType.Failure);
    }

    [Fact]
    public async Task Handle_ShouldRecordExito_WithTheTenantTheHandlerReported()
    {
        Result<string> result = await CreateBehavior().Handle(
            new SampleAuditedQuery(),
            _ =>
            {
                _scope.TenantDelRecurso(new TenantId("ARL_B"));
                return Task.FromResult(Result.Success("ok"));
            },
            CancellationToken.None);

        result.Value.ShouldBe("ok");
        await _writer.Received(1).RegistrarAsync(
            AccionAuditoria.Leer,
            Arg.Is<ObjetivoAuditoria>(o => o.Tipo == RecursoAuditoria.Caso && o.CasoMedicoId == CasoId),
            new TenantId("ARL_B"),
            ResultadoAuditoria.Exito,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ErrorType.NotFound, ResultadoAuditoria.NoEncontrado)]
    [InlineData(ErrorType.Forbidden, ResultadoAuditoria.Denegado)]
    [InlineData(ErrorType.Conflict, ResultadoAuditoria.Error)]
    [InlineData(ErrorType.Failure, ResultadoAuditoria.Error)]
    public async Task Handle_ShouldClassifyFailures_AndReturnThemUnchanged(ErrorType tipo, ResultadoAuditoria esperado)
    {
        var error = new Error("X.Code", "description", tipo);

        Result<string> result = await CreateBehavior().Handle(
            new SampleAuditedQuery(),
            _ => Task.FromResult(Result.Failure<string>(error)),
            CancellationToken.None);

        result.Error.ShouldBe(error);
        await _writer.Received(1).RegistrarAsync(
            AccionAuditoria.Leer, Arg.Any<ObjetivoAuditoria>(), null, esperado, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldRecordTheReadBeforeReturningIt()
    {
        var orden = new List<string>();
        _writer
            .RegistrarAsync(default, default!, default, default, default)
            .ReturnsForAnyArgs(_ =>
            {
                orden.Add("audit");
                return Task.CompletedTask;
            });

        await CreateBehavior().Handle(
            new SampleAuditedQuery(),
            _ =>
            {
                orden.Add("handler");
                return Task.FromResult(Result.Success("ok"));
            },
            CancellationToken.None);

        orden.ShouldBe(["handler", "audit"]);
    }
}
