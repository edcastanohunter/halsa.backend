namespace Halsa.Backend.Api.Extensions;

/// <summary>
/// CORS para el SPA del backoffice (<c>halsa-backoffice.alcast.dev</c>), que llama a la API desde otro origen con
/// <c>Authorization: Bearer</c>. Los orígenes salen de <c>Cors:OrigenesPermitidos</c> (en producción,
/// <c>Cors__OrigenesPermitidos__0</c>, <c>__1</c>...); sin ninguno, no se registra CORS y el navegador bloquea todo.
/// La API autentica por cabecera, no por cookie: no se permiten credenciales.
/// </summary>
public static class CorsExtensions
{
    public const string Politica = "backoffice";

    public const string Clave = "Cors:OrigenesPermitidos";

    /// <summary>Las respuestas que el SPA debe poder leer: el reto de step-up (RFC 9470), la descarga y el alta.</summary>
    private static readonly string[] CabecerasExpuestas = ["WWW-Authenticate", "Content-Disposition", "Location"];

    public static IServiceCollection AddCorsBackoffice(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        string[] origenes = Origenes(configuration);
        if (origenes.Length == 0)
        {
            return services;
        }

        services.AddCors(options => options.AddPolicy(Politica, policy => policy
            .WithOrigins(origenes)
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
            .AllowAnyHeader()
            .WithExposedHeaders(CabecerasExpuestas)
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

        return services;
    }

    /// <summary>
    /// Antes de la autenticación: el preflight <c>OPTIONS</c> no lleva token y se responde aquí. También así llevan sus
    /// cabeceras los <c>401</c> y <c>403</c> que dan después la autenticación y el tenant.
    /// </summary>
    public static IApplicationBuilder UseCorsBackoffice(this IApplicationBuilder app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app);

        return Origenes(configuration).Length == 0 ? app : app.UseCors(Politica);
    }

    private static string[] Origenes(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return [.. (configuration.GetSection(Clave).Get<string[]>() ?? [])
            .Where(origen => !string.IsNullOrWhiteSpace(origen))
            .Select(origen => origen.Trim().TrimEnd('/'))];
    }
}
