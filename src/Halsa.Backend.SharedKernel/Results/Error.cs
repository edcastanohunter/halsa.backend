using System.Text.Json.Serialization;

namespace Halsa.Backend.SharedKernel.Results;

/// <summary>
/// A machine-readable failure. Errors are values, never exceptions: exceptions are reserved
/// for things the caller cannot reasonably handle.
/// </summary>
[JsonConverter(typeof(ErrorJsonConverter))]
public record Error
{
    /// <summary>The absence of an error. Built through a private constructor because an empty code
    /// is exactly what the public one is there to reject.</summary>
    public static readonly Error None = new();

    public static readonly Error NullValue = new(
        "General.NullValue",
        "A null value was provided where a value was required.",
        ErrorType.Failure);

    /// <summary>Private: an empty code is exactly what the public constructor rejects.
    /// <see cref="ErrorJsonConverter"/> maps an empty code back to this instance when deserializing.</summary>
    private Error()
    {
        Code = string.Empty;
        Description = string.Empty;
        Type = ErrorType.Failure;
    }

    public Error(string code, string description, ErrorType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code, nameof(code));

        Code = code;
        Description = description;
        Type = type;
    }

    public string Code { get; }

    public string Description { get; }

    public ErrorType Type { get; }

    public static Error Failure(string code, string description) => new(code, description, ErrorType.Failure);

    public static Error Problem(string code, string description) => new(code, description, ErrorType.Problem);

    public static Error NotFound(string code, string description) => new(code, description, ErrorType.NotFound);

    public static Error Conflict(string code, string description) => new(code, description, ErrorType.Conflict);

    public static Error Forbidden(string code, string description) => new(code, description, ErrorType.Forbidden);

    public static Error Unauthorized(string code, string description) => new(code, description, ErrorType.Unauthorized);

    public static Error Unprocessable(string code, string description) => new(code, description, ErrorType.Unprocessable);

    public static Error Unavailable(string code, string description) => new(code, description, ErrorType.Unavailable);

    public static Error UnsupportedMediaType(string code, string description) =>
        new(code, description, ErrorType.UnsupportedMediaType);

    public static Error PayloadTooLarge(string code, string description) => new(code, description, ErrorType.PayloadTooLarge);

    public override string ToString() => $"{Code}: {Description}";
}
