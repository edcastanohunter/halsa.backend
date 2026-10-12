using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.EventSourcing;
using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.SharedKernel.Abstractions;
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
    internal DbSet<StoredEvent> Events => Set<StoredEvent>();

    internal DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    internal DbSet<CasoMedico> CasosMedicos => Set<CasoMedico>();

    // ── IReadDbContext ────────────────────────────────────────────────────────

    IQueryable<CasoMedico> IReadDbContext.CasosMedicos => CasosMedicos;

    // ── Model ─────────────────────────────────────────────────────────────────

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schemas.Default);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        // ── Query filter: mirrors the PostgreSQL RLS policy for CasoMedico. ────
        // When Resuelto is false (no HTTP context: migrations, seeds, integration
        // tests without an active scope) the filter passes all rows through.
        // At runtime, ARL actors filter by TenantId; IPS actors have no TenantId
        // but their RLS policy is enforced by the database, so we let all rows
        // through at the app level when only an IpsId is present.
        modelBuilder.Entity<CasoMedico>().HasQueryFilter(c =>
            !tenantContext.Resuelto ||
            (tenantContext.TenantId != null && c.TenantId == tenantContext.TenantId.Value) ||
            (tenantContext.TenantId == null && tenantContext.IpsId != null));

        base.OnModelCreating(modelBuilder);
    }
}
