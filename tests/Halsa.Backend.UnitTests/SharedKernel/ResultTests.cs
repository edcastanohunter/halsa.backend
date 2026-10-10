using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.UnitTests.SharedKernel;

public sealed class ResultTests
{
    [Fact]
    public void Success_ShouldNotCarryAnError()
    {
        Result result = Result.Success();

        result.IsSuccess.ShouldBeTrue();
        result.Error.ShouldBe(Error.None);
    }

    [Fact]
    public void Failure_ShouldRejectTheNoneError()
    {
        Should.Throw<ArgumentException>(() => Result.Failure(Error.None));
    }

    [Fact]
    public void Value_ShouldThrow_OnAFailedResult()
    {
        Result<int> result = Result.Failure<int>(Error.NotFound("X.NotFound", "missing"));

        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ImplicitConversion_ShouldProduceSuccess()
    {
        Result<string> result = "value";

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("value");
    }

    [Fact]
    public void ImplicitConversion_ShouldProduceFailure_ForNull()
    {
        Result<string> result = (string?)null;

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(Error.NullValue);
    }

    [Fact]
    public void Map_ShouldPropagateTheError()
    {
        Error error = Error.Conflict("X.Conflict", "clash");

        Result<int> mapped = Result.Failure<string>(error).Map(value => value.Length);

        mapped.IsFailure.ShouldBeTrue();
        mapped.Error.ShouldBe(error);
    }

    [Fact]
    public void Bind_ShouldChainSuccesses()
    {
        Result<int> result = Result.Success("42").Bind(value => Result.Success(int.Parse(value)));

        result.Value.ShouldBe(42);
    }

    [Fact]
    public void Ensure_ShouldFail_WhenThePredicateIsNotMet()
    {
        Error error = Error.Problem("X.TooSmall", "too small");

        Result<int> result = Result.Success(1).Ensure(value => value > 10, error);

        result.Error.ShouldBe(error);
    }

    [Fact]
    public void Match_ShouldSelectTheFailureBranch()
    {
        string outcome = Result.Failure<int>(Error.NotFound("X.NotFound", "missing"))
            .Match(value => $"ok:{value}", error => $"err:{error.Code}");

        outcome.ShouldBe("err:X.NotFound");
    }

    [Fact]
    public void ValidationError_ShouldCollectEveryFailure()
    {
        var validationError = ValidationError.FromResults(
        [
            Result.Failure(Error.Problem("A", "a")),
            Result.Success(),
            Result.Failure(Error.Problem("B", "b"))
        ]);

        validationError.Errors.Select(error => error.Code).ShouldBe(["A", "B"]);
        validationError.Type.ShouldBe(ErrorType.Validation);
    }
}
