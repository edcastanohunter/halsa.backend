using Halsa.Backend.Application.Abstractions.Tenancy;

namespace Halsa.Backend.Infrastructure.Tenancy;

/// <summary>
/// El contexto de tenant del scope de DI actual. Lo llenan <c>TenantContextMiddleware</c> (un request HTTP)
/// o <see cref="SystemTenantScope"/> (un proceso en segundo plano); <see cref="TenantContext"/> lo lee.
/// Scoped: cada request y cada mensaje del outbox tiene el suyo.
/// </summary>
public sealed class TenantContextHolder
{
    public TenantContextData? Data { get; private set; }

    /// <summary>Lanza si el scope ya tiene un contexto: nunca se reemplaza en silencio.</summary>
    public void Establecer(TenantContextData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (Data is not null)
        {
            throw new InvalidOperationException("Este scope ya tiene un contexto de tenant; no se puede reemplazar.");
        }

        Data = data;
    }

    internal void Limpiar() => Data = null;
}
