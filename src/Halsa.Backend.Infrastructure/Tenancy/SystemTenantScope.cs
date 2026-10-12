using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Halsa.Backend.Infrastructure.Tenancy;

/// <summary>
/// Llena el <see cref="TenantContextHolder"/> con el actor de sistema. Se niega dentro de un request HTTP:
/// ahí el actor es el del token, y suplantarlo por el sistema saltaría la autorización.
/// </summary>
internal sealed class SystemTenantScope(TenantContextHolder holder, IHttpContextAccessor httpContextAccessor)
    : ISystemTenantScope
{
    /// <summary>El <c>sub</c> (y el <c>actor_id</c> de la auditoría) del actor de sistema.</summary>
    public const string Sub = "sistema";

    public IDisposable Entrar(TenantId tenant)
    {
        if (httpContextAccessor.HttpContext is not null)
        {
            throw new InvalidOperationException(
                "No se puede entrar en un contexto de sistema dentro de un request HTTP.");
        }

        holder.Establecer(new TenantContextData(ActorTipo.Sistema, Sub, tenant, null));
        return new Salida(holder);
    }

    private sealed class Salida(TenantContextHolder holder) : IDisposable
    {
        private bool _liberado;

        public void Dispose()
        {
            if (_liberado)
            {
                return;
            }

            _liberado = true;
            holder.Limpiar();
        }
    }
}
