namespace Halsa.Backend.SharedKernel.Results;

/// <summary>
/// Classifies an <see cref="Error"/> so that transport layers (HTTP, gRPC, messaging)
/// can translate it without the application layer knowing about them.
/// </summary>
public enum ErrorType
{
    Failure = 0,
    Validation = 1,
    Problem = 2,
    NotFound = 3,
    Conflict = 4,
    Forbidden = 5,
    Unauthorized = 6,

    /// <summary>The request is well formed but breaks a business rule (SPEC 07: the incapacidad rules).</summary>
    Unprocessable = 7,

    /// <summary>A dependency the operation cannot do without is down (SPEC 07: the antivirus). Retry later.</summary>
    Unavailable = 8,

    /// <summary>The content is not of the accepted type (SPEC 07: a support that is not a PDF).</summary>
    UnsupportedMediaType = 9,

    /// <summary>The content exceeds the accepted size (SPEC 07: a support larger than 10 MB).</summary>
    PayloadTooLarge = 10,

    /// <summary>The resource is locked and the request cannot succeed until someone unlocks it (SPEC 12: a blocked code).</summary>
    Locked = 11,

    /// <summary>The caller exceeded a rate limit (SPEC 12: the failed redemptions of a patient). Retry later.</summary>
    TooManyRequests = 12
}
