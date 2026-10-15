using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Halsa.Backend.Infrastructure.Proceso;

/// <summary>
/// Qué hace este proceso (SPEC 09, <c>Halsa:Rol</c>). La API y el Worker son la misma imagen: el rol decide qué se
/// registra, así que escalar la API no multiplica los jobs.
/// </summary>
public enum RolProceso
{
    /// <summary>Los endpoints, el middleware y <c>/health</c>. Ningún hosted service de background.</summary>
    Api,

    /// <summary><c>OutboxProcessor</c>, Quartz y sus jobs, y solo <c>/health</c>. Ningún endpoint de negocio.</summary>
    Worker,

    /// <summary>Todo. El valor por defecto solo en <c>Development</c>.</summary>
    Ambos,

    /// <summary>
    /// SPEC 13: aplica las migraciones de los dos contextos y las particiones de auditoría, y termina. Nunca levanta el
    /// host: es el paso previo de cada despliegue (<c>docker compose run --rm migrador</c>).
    /// </summary>
    Migrador
}

public static class RolesProceso
{
    public const string Clave = "Halsa:Rol";

    /// <summary>
    /// El rol configurado en <c>Halsa:Rol</c>. Sin valor, <see cref="RolProceso.Ambos"/> en <c>Development</c>; en
    /// cualquier otro entorno es obligatorio, para que un despliegue nunca arranque jobs por omisión.
    /// </summary>
    public static RolProceso Resolver(IConfiguration configuration, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? valor = configuration[Clave];

        if (string.IsNullOrWhiteSpace(valor))
        {
            return string.Equals(environmentName, Environments.Development, StringComparison.Ordinal)
                ? RolProceso.Ambos
                : throw new InvalidOperationException(
                    $"'{Clave}' es obligatorio fuera de Development: Api, Worker, Migrador o Ambos.");
        }

        // By name only: Enum.TryParse would also take "2".
        foreach (RolProceso rol in Enum.GetValues<RolProceso>())
        {
            if (string.Equals(valor.Trim(), rol.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return rol;
            }
        }

        throw new InvalidOperationException($"'{Clave}' = '{valor}' no es Api, Worker, Migrador ni Ambos.");
    }

    /// <summary>
    /// El valor configurado es <see cref="RolProceso.Migrador"/>. Se lee antes que todo lo demás, porque el Migrador no
    /// necesita ni valida el resto de la configuración.
    /// </summary>
    public static bool EsMigrador(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return string.Equals(configuration[Clave]?.Trim(), nameof(RolProceso.Migrador), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Expone los endpoints de negocio.</summary>
    public static bool AtiendeApi(this RolProceso rol) => rol is RolProceso.Api or RolProceso.Ambos;

    /// <summary>Ejecuta el outbox y los jobs de Quartz.</summary>
    public static bool EjecutaWorker(this RolProceso rol) => rol is RolProceso.Worker or RolProceso.Ambos;
}
