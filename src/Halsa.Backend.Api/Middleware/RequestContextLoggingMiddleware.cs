using Serilog.Context;

namespace Halsa.Backend.Api.Middleware;

/// <summary>
/// Pushes the correlation id onto the Serilog context so every log line emitted while handling the
/// request can be traced back to it.
/// </summary>
internal sealed class RequestContextLoggingMiddleware(RequestDelegate next)
{
    private const string CorrelationIdHeaderName = "X-Correlation-Id";

    public Task Invoke(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        using (LogContext.PushProperty("CorrelationId", GetCorrelationId(context)))
        {
            return next(context);
        }
    }

    private static string GetCorrelationId(HttpContext context)
    {
        context.Request.Headers.TryGetValue(CorrelationIdHeaderName, out Microsoft.Extensions.Primitives.StringValues id);

        return id.FirstOrDefault() ?? context.TraceIdentifier;
    }
}
