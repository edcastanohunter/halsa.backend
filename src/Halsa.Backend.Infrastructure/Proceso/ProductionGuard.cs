using Halsa.Backend.Application.Abstractions.Integraciones;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Infrastructure.Authentication;
using Halsa.Backend.Infrastructure.Clinico.Crypto;
using Halsa.Backend.Infrastructure.Integraciones;
using Halsa.Backend.Infrastructure.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Halsa.Backend.Infrastructure.Proceso;

/// <summary>
/// SPEC 13: en <c>Production</c> la API y el Worker no arrancan con una configuración de desarrollo. Cada condición es un
/// atajo legítimo en local que en el VPS abriría un hueco, y el mensaje la nombra para que el despliegue diga qué
/// corregir. El proceso termina con <c>1</c> antes de levantar el host (<see cref="Abortar"/>).
///
/// <see cref="Verificar"/> lee solo la configuración, así que corre antes de resolver el rol;
/// <see cref="VerificarServicios"/> necesita el contenedor ya construido (el stub de afiliación).
/// </summary>
public static class ProductionGuard
{
    private static readonly string[] Emisores =
    [
        $"{KeycloakOptions.SectionName}:{nameof(KeycloakOptions.PersonalAuthority)}",
        $"{KeycloakOptions.SectionName}:{nameof(KeycloakOptions.PacientesAuthority)}"
    ];

    private static readonly string[] UsuariosPrivilegiados = ["halsa_owner", "postgres"];

    /// <summary>Las condiciones de configuración que se incumplen; vacía si la configuración es apta para producción.</summary>
    public static IReadOnlyList<string> Verificar(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var fallas = new List<string>();

        const string https = $"{KeycloakOptions.SectionName}:{nameof(KeycloakOptions.RequireHttpsMetadata)}";
        if (!(configuration.GetValue<bool?>(https) ?? true))
        {
            fallas.Add($"{https}=false: los metadatos de Keycloak solo se piden por https.");
        }

        foreach (string clave in Emisores)
        {
            string? emisor = configuration[clave];
            if (!Uri.TryCreate(emisor, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                fallas.Add($"{clave}='{emisor}': el emisor debe ser https.");
            }
        }

        string? rol = configuration[RolesProceso.Clave]?.Trim();
        if (string.IsNullOrEmpty(rol) || string.Equals(rol, nameof(RolProceso.Ambos), StringComparison.OrdinalIgnoreCase))
        {
            fallas.Add($"{RolesProceso.Clave}='{rol}': en producción cada contenedor es Api, Worker o Migrador, nunca vacío ni Ambos.");
        }

        const string certificado = $"{IntegracionesOptions.SectionName}:{nameof(IntegracionesOptions.ExigirCertificado)}";
        if (!configuration.GetValue<bool>(certificado))
        {
            fallas.Add($"{certificado}=false: los tokens de integración deben estar ligados al certificado (RFC 8705).");
        }

        const string destinosLocales = $"{WebhooksOptions.SectionName}:{nameof(WebhooksOptions.PermitirDestinosLocales)}";
        if (configuration.GetValue<bool>(destinosLocales))
        {
            fallas.Add($"{destinosLocales}=true: abriría el SSRF de los webhooks hacia la red interna.");
        }

        const string kekEnLinea = $"{ClinicoOptions.SectionName}:{nameof(ClinicoOptions.KekBase64)}";
        if (!string.IsNullOrEmpty(configuration[kekEnLinea]))
        {
            fallas.Add($"{kekEnLinea} tiene valor: la KEK solo viene del secreto de Clinico:KekPath.");
        }

        string? usuario = UsuarioDe(configuration.GetConnectionString("Database"));
        if (usuario is not null && UsuariosPrivilegiados.Contains(usuario, StringComparer.OrdinalIgnoreCase))
        {
            fallas.Add($"ConnectionStrings:Database usa '{usuario}': la aplicación se conecta como halsa_app, nunca como dueño ni superusuario.");
        }

        return fallas;
    }

    /// <summary>Las condiciones que dependen de lo registrado: hoy, que el stub de afiliación no responda.</summary>
    public static IReadOnlyList<string> VerificarServicios(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        using IServiceScope scope = services.CreateScope();

        return scope.ServiceProvider.GetService<IAfiliacionGateway>() is DevAfiliacionGateway
            ? ["El stub de afiliación (DevAfiliacionGateway) está registrado: aprobaría casos sin consultar a la ARL."]
            : [];
    }

    /// <summary>Escribe las condiciones incumplidas y devuelve el código de salida del proceso.</summary>
    public static int Abortar(IReadOnlyList<string> fallas, TextWriter salida)
    {
        ArgumentNullException.ThrowIfNull(fallas);
        ArgumentNullException.ThrowIfNull(salida);

        salida.WriteLine("ProductionGuard: la configuración no es apta para Production:");
        foreach (string falla in fallas)
        {
            salida.WriteLine($"  - {falla}");
        }

        return 1;
    }

    private static string? UsuarioDe(string? cadena)
    {
        if (string.IsNullOrWhiteSpace(cadena))
        {
            return null;
        }

        try
        {
            return new NpgsqlConnectionStringBuilder(cadena).Username;
        }
        catch (ArgumentException)
        {
            // Malformed: AddPersistence fails on it anyway, with Npgsql's own message.
            return null;
        }
    }
}
