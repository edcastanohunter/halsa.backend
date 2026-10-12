using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Infrastructure.Tenancy;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Halsa.Backend.UnitTests.Tenancy;

/// <summary>SPEC 05, paso 3: el contexto de tenant de sistema y su lectura por <see cref="TenantContext"/>.</summary>
public sealed class SystemTenantScopeTests
{
    private readonly TenantContextHolder _holder = new();
    private readonly IHttpContextAccessor _accessor = new HttpContextAccessor();

    private ITenantContext Contexto => new TenantContext(_holder);

    private SystemTenantScope Scope => new(_holder, _accessor);

    [Fact]
    public void Entrar_ShouldResolveTheSystemActor_ForTheTenant()
    {
        using (Scope.Entrar(new TenantId("ARL_A")))
        {
            Contexto.Resuelto.ShouldBeTrue();
            Contexto.Tipo.ShouldBe(ActorTipo.Sistema);
            Contexto.Sub.ShouldBe("sistema");
            Contexto.TenantId.ShouldBe(new TenantId("ARL_A"));
            Contexto.IpsId.ShouldBeNull();
        }
    }

    [Fact]
    public void Dispose_ShouldLeaveTheContextUnresolved()
    {
        IDisposable entrada = Scope.Entrar(new TenantId("ARL_A"));
        entrada.Dispose();

        Contexto.Resuelto.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => Contexto.Tipo);
    }

    [Fact]
    public void Entrar_InsideAnHttpRequest_ShouldThrow()
    {
        _accessor.HttpContext = new DefaultHttpContext();

        Should.Throw<InvalidOperationException>(() => Scope.Entrar(new TenantId("ARL_A")));
        Contexto.Resuelto.ShouldBeFalse();
    }

    [Fact]
    public void Entrar_WhenTheScopeAlreadyHasAContext_ShouldThrow()
    {
        _holder.Establecer(new TenantContextData(ActorTipo.Arl, "sub-arl", new TenantId("ARL_B"), null));

        Should.Throw<InvalidOperationException>(() => Scope.Entrar(new TenantId("ARL_A")));
        Contexto.TenantId.ShouldBe(new TenantId("ARL_B"));
    }

    [Fact]
    public void TenantContext_WithoutData_ShouldBeUnresolved_AndThrowOnAccess()
    {
        Contexto.Resuelto.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => Contexto.Sub);
        Should.Throw<InvalidOperationException>(() => Contexto.TenantId);
    }
}
