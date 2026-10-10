using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.WorkItems.ReadModels;
using Halsa.Backend.Infrastructure.EventSourcing;
using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.SharedKernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Infrastructure.Database;

/// <summary>
/// The single write connection: the event store, the outbox and the read models all live here so
/// that appending events, projecting them and enqueuing integration events commit atomically.
/// </summary>
public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : DbContext(options), IReadDbContext, IUnitOfWork
{
    internal DbSet<StoredEvent> Events => Set<StoredEvent>();

    internal DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    internal DbSet<WorkItemView> WorkItemViews => Set<WorkItemView>();

    /// <summary>Read side: no tracking, no writes.</summary>
    IQueryable<WorkItemView> IReadDbContext.WorkItems => Set<WorkItemView>().AsNoTracking();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schemas.Default);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
