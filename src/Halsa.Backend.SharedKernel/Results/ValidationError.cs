namespace Halsa.Backend.SharedKernel.Results;

/// <summary>
/// Aggregates one or more validation failures into a single <see cref="Error"/>.
/// </summary>
public sealed record ValidationError : Error
{
    public ValidationError(IReadOnlyList<Error> errors)
        : base("General.Validation", "One or more validation errors occurred.", ErrorType.Validation)
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = errors;
    }

    public IReadOnlyList<Error> Errors { get; }

    public static ValidationError FromResults(IEnumerable<Result> results) =>
        new([.. results.Where(r => r.IsFailure).Select(r => r.Error)]);
}
