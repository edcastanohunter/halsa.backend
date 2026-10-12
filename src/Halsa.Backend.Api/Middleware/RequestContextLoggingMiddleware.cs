using Halsa.Backend.Infrastructure.Http;
using Serilog.Context;

namespace Halsa.Backend.Api.Middleware;

/// <summary>
/// Pushes the correlation id onto the Serilog context so every log line emitted while handling the
/// request can be traced back to it. The audit trail records the same id (<see cref="CorrelationId"/>).
/// </summary>
internal sealed class RequestContextLoggingMiddleware(RequestDelegate next)
{
    public Task Invoke(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        using (LogContext.PushProperty("CorrelationId", CorrelationId.From(context)))
        {
            return next(context);
        }
    }
}
