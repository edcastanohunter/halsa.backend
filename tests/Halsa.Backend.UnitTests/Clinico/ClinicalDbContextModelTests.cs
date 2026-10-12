using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.Database.Clinico;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.UnitTests.Clinico;

/// <summary>
/// The clinical model and its query filter, checked without a database: <c>ToQueryString</c> only
/// needs the provider. The integration tests prove the same filter against PostgreSQL.
/// </summary>
public sealed class ClinicalDbContextModelTests
{
    private static ClinicalDbContext Context(ITenantContext tenantContext) =>
        new(new DbContextOptionsBuilder<ClinicalDbContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options,
            tenantContext);

    private static ITenantContext Actor(TenantId? tenant, IpsId? ips)
    {
        ITenantContext context = Substitute.For<ITenantContext>();
        context.Resuelto.Returns(true);
        context.Tipo.Returns(ips is null ? ActorTipo.Arl : ActorTipo.Ips);
        context.Sub.Returns("sub-test");
        context.TenantId.Returns(tenant);
        context.IpsId.Returns(ips);
        return context;
    }

    [Fact]
    public void Model_ShouldMapEveryEntityToTheClinicoSchema()
    {
        using ClinicalDbContext db = Context(Actor(new TenantId("ARL_A"), null));

        db.Model.GetEntityTypes()
            .Select(e => e.GetSchema())
            .Distinct()
            .ShouldBe([Schemas.Clinico]);
    }

    [Fact]
    public void QueryFilter_ShouldCallTheClinicoWrapperFunction_ForTheIpsBranch()
    {
        using ClinicalDbContext db = Context(Actor(null, new IpsId("110011234501")));

        string sql = db.HistoriaClinica
            .Where(h => h.CasoMedicoId == new CasoMedicoId(Guid.CreateVersion7()))
            .ToQueryString();

        sql.ShouldContain("clinico.tenant_del_caso_remitido(h.caso_medico_id, ");
        sql.ShouldContain("h.tenant_id = @");
        sql.ShouldNotContain("atel.");
    }
}
