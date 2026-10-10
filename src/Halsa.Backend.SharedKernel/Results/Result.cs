using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Halsa.Backend.SharedKernel.Results;

/// <summary>
/// The outcome of an operation that can fail for expected, domain-level reasons.
/// </summary>
public class Result
{
    [JsonConstructor]
    public Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None)
        {
            throw new ArgumentException("A successful result cannot carry an error.", nameof(error));
        }

        if (!isSuccess && error == Error.None)
        {
            throw new ArgumentException("A failed result must carry an error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);

    public static Result<TValue> Create<TValue>(TValue? value) =>
        value is not null ? Success(value) : Failure<TValue>(Error.NullValue);

    /// <summary>Returns the first failure, or success when every result succeeded.</summary>
    public static Result FirstFailureOrSuccess(params Result[] results)
    {
        ArgumentNullException.ThrowIfNull(results);

        foreach (Result result in results)
        {
            if (result.IsFailure)
            {
                return Failure(result.Error);
            }
        }

        return Success();
    }

    public TOut Match<TOut>(Func<TOut> onSuccess, Func<Error, TOut> onFailure) =>
        IsSuccess ? onSuccess() : onFailure(Error);
}

/// <inheritdoc cref="Result" />
public class Result<TValue> : Result
{
    private readonly TValue? _value;

    [JsonConstructor]
    public Result(TValue? value, bool isSuccess, Error error)
        : base(isSuccess, error) => _value = value;

    [NotNull]
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("The value of a failed result cannot be accessed.");

    public static implicit operator Result<TValue>(TValue? value) => Create(value);

    public TOut Match<TOut>(Func<TValue, TOut> onSuccess, Func<Error, TOut> onFailure) =>
        IsSuccess ? onSuccess(Value) : onFailure(Error);

    public Result<TOut> Map<TOut>(Func<TValue, TOut> map) =>
        IsSuccess ? Success(map(Value)) : Failure<TOut>(Error);

    public Result<TOut> Bind<TOut>(Func<TValue, Result<TOut>> bind) =>
        IsSuccess ? bind(Value) : Failure<TOut>(Error);

    public Result<TValue> Ensure(Func<TValue, bool> predicate, Error error) =>
        IsFailure || predicate(Value) ? this : Failure<TValue>(error);
}
