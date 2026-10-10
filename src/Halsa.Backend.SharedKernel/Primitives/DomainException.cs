namespace Halsa.Backend.SharedKernel.Primitives;

/// <summary>
/// Thrown only when an invariant is violated in a way the caller could not have anticipated.
/// Expected, handleable failures are returned as <see cref="Results.Result"/> instead.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message) : base(message)
    {
    }

    public DomainException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
