using Halsa.Backend.SharedKernel.EventSourcing;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Halsa.Backend.Api.Infrastructure;

/// <summary>
/// Last line of defence. Everything the application can foresee is a <c>Result</c>; anything that
/// reaches here is a bug or an infrastructure failure, and the client only learns that much.
/// </summary>
internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        logger.LogError(exception, "Unhandled exception for {Path}", httpContext.Request.Path);

        (int statusCode, string title) = exception switch
        {
            ConcurrencyException => (StatusCodes.Status409Conflict, "Concurrent modification"),
            OperationCanceledException => (499, "Request cancelled"), // client closed request
            _ => (StatusCodes.Status500InternalServerError, "Server failure")
        };

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Type = exception.GetType().Name,
            Instance = httpContext.Request.Path
        };

        httpContext.Response.StatusCode = statusCode;

        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken).ConfigureAwait(false);

        return true;
    }
}
