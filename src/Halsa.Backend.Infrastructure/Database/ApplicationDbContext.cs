using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Integraciones.Registros;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Incapacidades;
using Halsa.Backend.Infrastructure.Database.Auditoria;
using Halsa.Backend.Infrastructure.Database.Integraciones;
using Halsa.Backend.Infrastructure.EventSourcing;
using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.SharedKernel.Abstractions;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Infrastructure.Database;

/// <summary>
/// The single write connection: the event store, the outbox and the domain tables all live here so
/// that domain writes, outbox enqueuing and tenant session commit atomically.
///
/// <paramref name="tenantContext"/> drives the EF-level query filter that mirrors the PostgreSQL
/// RLS policies — defence in depth. Use <see cref="Tenancy.NullTenantContext"/> when creating
/// the context outside a request (migrations, seeds).
/// </summary>
public sealed class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options,
    ITenantContext tenantContext)
    : DbContext(options), IReadDbContext, IUnitOfWork
{
    private static readonly string? ConfigurationsNamespace = typeof(Configurations.CasoMedicoConfiguration).Namespace;

    internal DbSet<StoredEvent> Events => Set<StoredEvent>();

    internal DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    internal DbSet<CasoMedico> CasosMedicos => Set<CasoMedico>();

    internal DbSet<AccesoAuditado> AccesosAuditados => Set<AccesoAuditado>();

    internal DbSet<SolicitudIntegracion> Solicitudes => Set<SolicitudIntegracion>();

    internal DbSet<Incapacidad> Incapacidades => Set<Incapacidad>();

    // ── IReadDbContext ────────────────────────────────────────────────────────

    IQueryable<CasoMedico> IReadDbContext.CasosMedicos => CasosMedicos;

    IQueryable<SolicitudIntegracion> IReadDbContext.Solicitudes => Solicitudes.AsNoTracking();

    IQueryable<Incapacidad> IReadDbContext.Incapacidades => Incapacidades.AsNoTracking();

    // ── Model ─────────────────────────────────────────────────────────────────

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schemas.Default);

        // Only this context's namespace: the clinical configurations live in the same assembly and
        // must never reach a model that connects as halsa_app (SPEC 03).
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(ApplicationDbContext).Assembly,
            type => type.Namespace == ConfigurationsNamespace);

        // Shared with ClinicalDbContext, so it lives outside the scanned namespace. This context owns the table.
        modelBuilder.ApplyConfiguration(new AccesoAuditadoConfiguration());

        // Las tablas de la ingesta (SPEC 05): este contexto las crea en sus migraciones, IngestaDbContext las
        // escribe como halsa_ingesta. halsa_app solo tiene SELECT sobre integracion.solicitudes.
        modelBuilder.ApplyConfiguration(new ReporteEventoConfiguration());
        modelBuilder.ApplyConfiguration(new SolicitudIntegracionConfiguration());
        modelBuilder.ApplyConfiguration(new SolicitudIdempotenteConfiguration());
        modelBuilder.ApplyConfiguration(new MensajeEntranteConfiguration());

        // ── Query filter: mirrors the PostgreSQL RLS policies for CasoMedico. ────
        // Fails closed: with no resolved context neither branch matches and no rows
        // come back, exactly like RLS with empty app.* variables.
        modelBuilder.Entity<CasoMedico>().HasQueryFilter(c =>
            (TieneTenant && c.TenantId == CurrentTenant) ||
            (TieneIps && c.Remisiones.Any(r => r.IpsId == CurrentIps && r.RevocadaEn == null)));

        // Mirrors the RLS of atel.incapacidades (SPEC 07): the tenant's own, or those of a case remitted to the IPS.
        // The case's own filter already encodes the live remission.
        modelBuilder.Entity<Incapacidad>().HasQueryFilter(i =>
            (TieneTenant && i.TenantId == CurrentTenant) ||
            (TieneIps && CasosMedicos.Any(c => c.Id == i.CasoMedicoId)));

        // Mirrors the RLS of integracion.solicitudes: only the tenant's own requests.
        modelBuilder.Entity<SolicitudIntegracion>().HasQueryFilter(s => TieneTenant && s.TenantId == CurrentTenant);

        base.OnModelCreating(modelBuilder);
    }

    // EF evaluates these per query as parameters. The flags guard the comparisons so the
    // default (null-valued) ids never have to match anything.
    private bool TieneTenant => tenantContext.Resuelto && tenantContext.TenantId is not null;

    private TenantId CurrentTenant => TieneTenant ? tenantContext.TenantId!.Value : default;

    private bool TieneIps => tenantContext.Resuelto && tenantContext.IpsId is not null;

    private IpsId CurrentIps => TieneIps ? tenantContext.IpsId!.Value : default;
}
