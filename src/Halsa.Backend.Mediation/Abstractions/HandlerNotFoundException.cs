namespace Halsa.Backend.Mediation;

/// <summary>
/// Raised when a request reaches the mediator but no handler was registered for it.
/// Almost always a missing <c>RegisterServicesFromAssembly</c> call.
/// </summary>
public sealed class HandlerNotFoundException : InvalidOperationException
{
    public HandlerNotFoundException(Type requestType)
        : base($"No handler was registered for request '{requestType.FullName}'. " +
               "Register the assembly that contains it via AddMediation(cfg => cfg.RegisterServicesFromAssemblyContaining<T>()).") =>
        RequestType = requestType;

    public Type RequestType { get; }
}

/// <summary>Raised when more than one handler claims the same request.</summary>
public sealed class DuplicateHandlerException : InvalidOperationException
{
    public DuplicateHandlerException(Type requestType, Type existing, Type duplicate)
        : base($"Request '{requestType.FullName}' is handled by both '{existing.FullName}' and " +
               $"'{duplicate.FullName}'. A request must have exactly one handler; use a notification " +
               "when several components need to react.") =>
        RequestType = requestType;

    public Type RequestType { get; }
}
