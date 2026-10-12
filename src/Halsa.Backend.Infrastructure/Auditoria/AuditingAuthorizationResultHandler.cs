using Halsa.Backend.Application.Abstractions.Auditoria;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Halsa.Backend.Infrastructure.Auditoria;

/// <summary>
/// Registra <c>DENEGADO</c> cuando una política rechaza (forbid o challenge) un request a una ruta
/// <see cref="AuditableAttribute"/>, y luego deja que el handler por defecto responda.
/// El tenant del recurso no se conoce sin ejecutar el handler, así que la fila va con <c>tenant_id = null</c>.
///
/// Solo las rutas marcadas: auditar cada 401 de la API taparía la señal con ruido.
/// </summary>
internal sealed partial class AuditingAuthorizationResultHandler(ILogger<AuditingAuthorizationResultHandler> logger)
    : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if ((authorizeResult.Forbidden || authorizeResult.Challenged) &&
            context.GetEndpoint()?.Metadata.GetMetadata<AuditableAttribute>() is { } auditable)
        {
            await RegistrarDenegadoAsync(context, auditable);
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }

    private async Task RegistrarDenegadoAsync(HttpContext context, AuditableAttribute auditable)
    {
        Guid? casoId = Guid.TryParse(
            context.Request.RouteValues[AuditableAttribute.CasoIdRouteValue]?.ToString(), out Guid id)
            ? id
            : null;

        var objetivo = new ObjetivoAuditoria(auditable.Recurso, casoId?.ToString(), casoId);

        try
        {
            await context.RequestServices
                .GetRequiredService<IAuditWriter>()
                .RegistrarAsync(
                    auditable.AccionPara(context.Request.Method),
                    objetivo,
                    tenant: null,
                    ResultadoAuditoria.Denegado,
                    context.RequestAborted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The request is refused either way; a lost denial is logged, never turned into a 500.
            LogDenegadoNoRegistrado(logger, exception, context.Request.Path);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "No se pudo auditar el acceso denegado a {Path}")]
    private static partial void LogDenegadoNoRegistrado(ILogger logger, Exception exception, PathString path);
}
