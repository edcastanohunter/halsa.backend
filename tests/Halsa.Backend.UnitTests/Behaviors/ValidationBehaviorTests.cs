using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Behaviors;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using FluentValidation;
using FluentValidation.Results;

namespace Halsa.Backend.UnitTests.Behaviors;

public sealed class ValidationBehaviorTests
{
    public sealed record SampleCommand(string Title) : ICommand<Guid>;

    public sealed record VoidCommand : ICommand;

    [Fact]
    public async Task Handle_ShouldCallTheHandler_WhenThereAreNoValidators()
    {
        var behavior = new ValidationBehavior<SampleCommand, Result<Guid>>([]);
        Guid expected = Guid.CreateVersion7();
        var handlerCalled = false;

        Result<Guid> result = await behavior.Handle(
            new SampleCommand("ok"),
            _ =>
            {
                handlerCalled = true;
                return Task.FromResult(Result.Success(expected));
            },
            CancellationToken.None);

        handlerCalled.ShouldBeTrue();
        result.Value.ShouldBe(expected);
    }

    [Fact]
    public async Task Handle_ShouldShortCircuit_WhenValidationFails()
    {
        IValidator<SampleCommand> validator = Substitute.For<IValidator<SampleCommand>>();

        validator
            .ValidateAsync(Arg.Any<ValidationContext<SampleCommand>>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult(
            [
                new ValidationFailure("Title", "Title is required") { ErrorCode = "WorkItems.TitleRequired" }
            ]));

        var behavior = new ValidationBehavior<SampleCommand, Result<Guid>>([validator]);
        var handlerCalled = false;

        Result<Guid> result = await behavior.Handle(
            new SampleCommand(""),
            _ =>
            {
                handlerCalled = true;
                return Task.FromResult(Result.Success(Guid.Empty));
            },
            CancellationToken.None);

        handlerCalled.ShouldBeFalse();
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeOfType<ValidationError>()
            .Errors.ShouldHaveSingleItem()
            .Code.ShouldBe("WorkItems.TitleRequired");
    }

    [Fact]
    public async Task Handle_ShouldReturnANonGenericFailure_ForVoidCommands()
    {
        IValidator<VoidCommand> validator = Substitute.For<IValidator<VoidCommand>>();

        validator
            .ValidateAsync(Arg.Any<ValidationContext<VoidCommand>>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult([new ValidationFailure("Id", "Id is required") { ErrorCode = "Id" }]));

        var behavior = new ValidationBehavior<VoidCommand, Result>([validator]);

        Result result = await behavior.Handle(
            new VoidCommand(),
            _ => Task.FromResult(Result.Success()),
            CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Validation);
    }
}
