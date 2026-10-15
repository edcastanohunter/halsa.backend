using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace Halsa.Bff.Paciente.Sesion;

/// <summary>
/// La sesión del portal, fuera de la cookie (SPEC 12): la cookie solo lleva la llave, y el ticket (con el access token, el
/// refresh token y el id token) vive en Redis, protegido con Data Protection. Así los tokens nunca llegan al navegador,
/// y borrar la entrada revoca la sesión. Cada entrada vence con el ticket: la inactividad la desliza la cookie, que
/// llama a <see cref="RenewAsync"/>.
/// </summary>
internal sealed class RedisTicketStore(IDistributedCache cache, IDataProtectionProvider dataProtection) : ITicketStore
{
    private const string Prefijo = "sesion:";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("Halsa.Bff.Paciente.Sesion.v1");

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        string llave = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await RenewAsync(llave, ticket);
        return llave;
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket) => RenewAsync(key, ticket, CancellationToken.None);

    public Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        byte[] protegido = _protector.Protect(TicketSerializer.Default.Serialize(ticket));
        var opciones = new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.AddMinutes(30)
        };

        return cache.SetAsync(Prefijo + key, protegido, opciones, cancellationToken);
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveAsync(key, CancellationToken.None);

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        byte[]? protegido = await cache.GetAsync(Prefijo + key, cancellationToken);
        if (protegido is null)
        {
            return null;
        }

        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(protegido));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Keys rotated away or a tampered entry: the session is simply gone.
            return null;
        }
    }

    public Task RemoveAsync(string key) => RemoveAsync(key, CancellationToken.None);

    public Task RemoveAsync(string key, CancellationToken cancellationToken) => cache.RemoveAsync(Prefijo + key, cancellationToken);
}

/// <summary>Conecta el store a las opciones de la cookie, que se resuelven antes del contenedor.</summary>
internal sealed class ConfigurarSesion(ITicketStore store) : Microsoft.Extensions.Options.IPostConfigureOptions<CookieAuthenticationOptions>
{
    public void PostConfigure(string? name, CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (name == CookieAuthenticationDefaults.AuthenticationScheme)
        {
            options.SessionStore = store;
        }
    }
}
