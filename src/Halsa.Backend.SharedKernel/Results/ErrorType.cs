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
    Unauthorized = 6
}
