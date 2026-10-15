using Microsoft.AspNetCore.Http.Features;

namespace Halsa.Bff.Paciente.Seguridad;

/// <summary>
/// Las dos guardas que corren antes de la autenticación (SPEC 12), en este orden:
/// <list type="number">
///   <item>Ruta: bajo <c>/paciente/api/</c> no se admite ningún segmento <c>.</c> o <c>..</c>, ni puntos, barras o
///   barras invertidas codificados: un intento de salir del prefijo responde <c>404</c> y nunca se reenvía.</item>
///   <item>Anti-CSRF: todo request a <c>/paciente/api/*</c> y el <c>POST</c> de logout exigen <c>X-Halsa-Csrf: 1</c>;
///   sin él, <c>403</c>. Junto con <c>SameSite=Strict</c>, un sitio ajeno no puede usar la sesión.</item>
/// </list>
/// </summary>
internal static class GuardasBff
{
    public static IApplicationBuilder UseGuardasBff(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            PathString ruta = context.Request.Path;
            bool esApi = ruta.StartsWithSegments(Bff.ApiPrefix, StringComparison.OrdinalIgnoreCase);

            if (esApi && !RutaSegura(context))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            bool esLogout = HttpMethods.IsPost(context.Request.Method) &&
                            ruta.Equals($"{Bff.BffPrefix}/logout", StringComparison.OrdinalIgnoreCase);

            if ((esApi || esLogout) && context.Request.Headers[Bff.CsrfHeader] != "1")
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });

    /// <summary>
    /// Mira la ruta decodificada y la que llegó en la línea del request: Kestrel ya normaliza los <c>..</c> literales,
    /// pero una prueba en memoria o un proxy intermedio pueden no hacerlo.
    /// </summary>
    internal static bool RutaSegura(HttpContext context)
    {
        string decodificada = context.Request.Path.Value ?? string.Empty;
        string cruda = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? decodificada;

        int consulta = cruda.IndexOf('?', StringComparison.Ordinal);
        if (consulta >= 0)
        {
            cruda = cruda[..consulta];
        }

        // Both forms: Kestrel leaves %2F encoded in Path, and a double-encoded %252F decodes to a literal "%2f".
        if (ConCodificados(cruda) || ConCodificados(decodificada))
        {
            return false;
        }

        foreach (string segmento in decodificada.Split('/').Concat(cruda.Split('/')))
        {
            if (segmento is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    private static bool ConCodificados(string ruta) =>
        ruta.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
        ruta.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
        ruta.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
        ruta.Contains('\\', StringComparison.Ordinal);
}
