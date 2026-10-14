using System.Globalization;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Halsa.Backend.Infrastructure.Auditoria;

/// <summary>
/// Registra <c>DENEGADO</c> cuando una política rechaza (forbid o challenge) un request a una ruta
/// <see cref="AuditableAttribute"/>, y luego deja que el handler por defecto responda.
/// El tenant del recurso no se conoce sin ejecutar el handler, así que la fila va con <c>tenant_id = null</c>.
///
/// Un rechazo cuyo único motivo es el step-up (<see cref="StepUpRequerido"/>, SPEC 07) no es un <c>403</c>: responde
/// <c>401</c> con <c>WWW-Authenticate: Bearer error="insufficient_user_authentication"</c> (RFC 9470), y la fila de
/// auditoría lleva <c>motivo='step-up'</c>.
///
/// Solo las rutas marcadas: auditar cada 401 de la API taparía la señal con ruido.
/// </summary>
internal sealed partial class AuditingAuthorizationResultHandler(ILogger<AuditingAuthorizationResultHandler> logger)
    : IAuthorizationMiddlewareResultHandler
{
    /// <summary>La columna <c>motivo</c> de un denegado por step-up.</summary>
    public const string MotivoStepUp = "step-up";

    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        StepUpRequerido? stepUp = authorizeResult.Forbidden
            ? await SoloStepUpAsync(context, policy, authorizeResult.AuthorizationFailure)
            : null;

        if ((authorizeResult.Forbidden || authorizeResult.Challenged) &&
            context.GetEndpoint()?.Metadata.GetMetadata<AuditableAttribute>() is { } auditable)
        {
            await RegistrarDenegadoAsync(context, auditable, stepUp is null ? null : MotivoStepUp);
        }

        if (stepUp is not null)
        {
            await ResponderStepUpAsync(context, stepUp);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }

    /// <summary>
    /// El <see cref="StepUpRequerido"/> si es lo único que falló. Si también falta un permiso, el rechazo es un
    /// <c>403</c>: subir el nivel de autenticación no lo arreglaría. Una vez que un handler llama a <c>Fail</c>, el
    /// resultado ya no dice qué otros requisitos quedaron pendientes, así que el resto de la política se evalúa de nuevo.
    /// </summary>
    private static async Task<StepUpRequerido?> SoloStepUpAsync(
        HttpContext context, AuthorizationPolicy policy, AuthorizationFailure? failure)
    {
        StepUpRequerido? stepUp = failure?.FailureReasons.OfType<StepUpRequerido>().FirstOrDefault();
        if (stepUp is null)
        {
            return null;
        }

        IAuthorizationRequirement[] resto = [.. policy.Requirements.Where(r => r is not StepUpRequirement)];
        if (resto.Length == 0)
        {
            return stepUp;
        }

        AuthorizationResult sinStepUp = await context.RequestServices
            .GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(context.User, resource: null, resto);

        return sinStepUp.Succeeded ? stepUp : null;
    }

    /// <summary>
    /// <c>Bearer error="insufficient_user_authentication", acr_values="2"</c>, más <c>max_age</c> si la política exige
    /// una autenticación reciente; y el problema con el código <see cref="StepUpRequerido.Codigo"/>.
    /// </summary>
    internal static string WwwAuthenticate(StepUpRequerido stepUp)
    {
        ArgumentNullException.ThrowIfNull(stepUp);

        // No error_description: a header value must be ASCII, and the message is in Spanish. It goes in the body.
        string valor = string.Create(CultureInfo.InvariantCulture,
            $"Bearer error=\"insufficient_user_authentication\", acr_values=\"{stepUp.Acr}\"");

        return stepUp.MaxAge is TimeSpan maxAge
            ? string.Create(CultureInfo.InvariantCulture, $"{valor}, max_age=\"{(long)maxAge.TotalSeconds}\"")
            : valor;
    }

    private static async Task ResponderStepUpAsync(HttpContext context, StepUpRequerido stepUp)
    {
        context.Response.Headers[HeaderNames.WWWAuthenticate] = WwwAuthenticate(stepUp);

        await Results.Problem(
                title: "Unauthorized",
                detail: stepUp.Message,
                type: "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                statusCode: StatusCodes.Status401Unauthorized,
                extensions: new Dictionary<string, object?> { ["errorCode"] = StepUpRequerido.Codigo })
            .ExecuteAsync(context);
    }

    private async Task RegistrarDenegadoAsync(HttpContext context, AuditableAttribute auditable, string? motivo)
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
                    context.RequestAborted,
                    motivo);
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
