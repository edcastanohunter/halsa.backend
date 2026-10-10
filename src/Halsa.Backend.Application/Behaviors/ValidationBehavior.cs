using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using FluentValidation;
using FluentValidation.Results;

namespace Halsa.Backend.Application.Behaviors;

/// <summary>
/// Runs every registered validator for a command before its handler. Validation failures come back
/// as a <see cref="ValidationError"/> result, never as an exception: they are an expected outcome.
/// <para>Queries are deliberately not validated - keep the read path cheap.</para>
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IBaseCommand
    where TResponse : Result
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        IValidator<TRequest>[] applicable = [.. validators];

        if (applicable.Length == 0)
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }

        var context = new ValidationContext<TRequest>(request);

        ValidationResult[] results = await Task.WhenAll(
            applicable.Select(validator => validator.ValidateAsync(context, cancellationToken)))
            .ConfigureAwait(false);

        ValidationFailure[] failures = [.. results
            .Where(result => !result.IsValid)
            .SelectMany(result => result.Errors)];

        if (failures.Length == 0)
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }

        var validationError = new ValidationError(
            [.. failures.Select(failure => Error.Problem(
                failure.ErrorCode ?? failure.PropertyName,
                failure.ErrorMessage))]);

        return ResultFactory.Failure<TResponse>(validationError);
    }
}
