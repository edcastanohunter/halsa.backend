using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Abstractions.Tenancy;

/// <summary>
/// El contexto de tenant de un proceso en segundo plano (SPEC 05): sin request HTTP no hay token del que
/// resolverlo, así que el proceso declara para qué tenant trabaja. Dentro del scope, <see cref="ITenantContext"/>
/// es <c>{ Resuelto, Tipo = Sistema, Sub = "sistema", TenantId = tenant }</c>, y la RLS y los query filters
/// se aplican igual que en un request.
/// </summary>
public interface ISystemTenantScope
{
    /// <summary>
    /// Entra en el contexto de sistema de <paramref name="tenant"/> hasta que se libere el valor devuelto.
    /// Lanza una <see cref="InvalidOperationException"/> dentro de un request HTTP, o si el scope de DI
    /// ya tiene un contexto.
    /// </summary>
    IDisposable Entrar(TenantId tenant);
}
