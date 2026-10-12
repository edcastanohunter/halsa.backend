using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Halsa.Backend.Infrastructure.Http;

/// <summary>
/// El id de correlación del request: la cabecera <c>X-Correlation-Id</c> o, si no viene, el
/// <c>TraceIdentifier</c>. Lo comparten los logs (<c>RequestContextLoggingMiddleware</c>) y la auditoría,
/// para que una fila de <c>auditoria.accesos</c> lleve a las líneas de log de su request.
/// </summary>
public static class CorrelationId
{
    public const string HeaderName = "X-Correlation-Id";

    public static string From(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Request.Headers.TryGetValue(HeaderName, out StringValues id);

        return id.FirstOrDefault() ?? context.TraceIdentifier;
    }
}
