using Halsa.Backend.Application.Behaviors;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.Extensions.Logging;

namespace Halsa.Backend.UnitTests.Behaviors;

public sealed class LoggingBehaviorTests
{
    public sealed record SampleRequest : IRequest<Result>;

    [Fact]
    public async Task Handle_ShouldReturnTheHandlerResponse()
    {
        ILogger<LoggingBehavior<SampleRequest, Result>> logger =
            Substitute.For<ILogger<LoggingBehavior<SampleRequest, Result>>>();

        var behavior = new LoggingBehavior<SampleRequest, Result>(logger);

        Result response = await behavior.Handle(
            new SampleRequest(),
            _ => Task.FromResult(Result.Success()),
            CancellationToken.None);

        response.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_ShouldSurfaceFailuresUnchanged()
    {
        ILogger<LoggingBehavior<SampleRequest, Result>> logger =
            Substitute.For<ILogger<LoggingBehavior<SampleRequest, Result>>>();

        var behavior = new LoggingBehavior<SampleRequest, Result>(logger);
        Error error = Error.Problem("X.Bad", "bad");

        Result response = await behavior.Handle(
            new SampleRequest(),
            _ => Task.FromResult(Result.Failure(error)),
            CancellationToken.None);

        response.Error.ShouldBe(error);
    }
}
