using Halsa.Backend.Api.Infrastructure;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Infrastructure.Tenancy;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Api.Middleware;

/// <summary>
/// Middleware que resuelve el contexto de tenant a partir del principal autenticado
/// y lo deposita en <c>HttpContext.Items</c> para que <c>HttpTenantContext</c> lo lea.
///
/// Posición en el pipeline: entre <c>UseAuthentication()</c> y <c>UseAuthorization()</c>.
/// Si el usuario es anónimo, el middleware deja pasar el request sin intervenir,
/// lo que permite que endpoints públicos (ej. <c>/health</c>) respondan normalmente.
/// Si el usuario está autenticado pero los claims son incompletos o inválidos,
/// responde con <c>403 Forbidden</c> y un ProblemDetails con el código de error específico.
/// </summary>
public sealed class TenantContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        TenantContextResolver resolver)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            Result<TenantContextData> resolveResult = resolver.Resolve(context.User);

            if (resolveResult.IsFailure)
            {
                IResult problem = CustomResults.Problem(resolveResult);
                await problem.ExecuteAsync(context);
                return;
            }

            context.Items[TenantContextKeys.Key] = resolveResult.Value;
        }

        await next(context);
    }
}
