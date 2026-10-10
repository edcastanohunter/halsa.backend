using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Application.WorkItems.ReadModels;
using Halsa.Backend.Domain.WorkItems;
using Halsa.Backend.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Infrastructure.Projections;

/// <summary>
/// Keeps the <see cref="WorkItemView"/> read model in step with the work item stream.
/// A projection is a pure fold: it must be idempotent, because at-least-once redelivery is normal.
/// </summary>
internal sealed class WorkItemProjection(ApplicationDbContext context) :
    IDomainEventHandler<WorkItemCreated>,
    IDomainEventHandler<WorkItemRenamed>,
    IDomainEventHandler<WorkItemCompleted>
{
    public async Task Handle(
        DomainEventNotification<WorkItemCreated> notification,
        CancellationToken cancellationToken)
    {
        WorkItemCreated domainEvent = notification.DomainEvent;

        WorkItemView? existing = await FindAsync(domainEvent.WorkItemId, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            return;
        }

        context.WorkItemViews.Add(new WorkItemView
        {
            Id = domainEvent.WorkItemId,
            Title = domainEvent.Title,
            Description = domainEvent.Description,
            Status = nameof(WorkItemStatus.Open),
            CreatedOnUtc = domainEvent.CreatedOnUtc,
            Version = 0
        });
    }

    public async Task Handle(
        DomainEventNotification<WorkItemRenamed> notification,
        CancellationToken cancellationToken)
    {
        WorkItemRenamed domainEvent = notification.DomainEvent;

        WorkItemView? view = await FindAsync(domainEvent.WorkItemId, cancellationToken).ConfigureAwait(false);

        if (view is null)
        {
            return;
        }

        view.Title = domainEvent.Title;
        view.Version++;
    }

    public async Task Handle(
        DomainEventNotification<WorkItemCompleted> notification,
        CancellationToken cancellationToken)
    {
        WorkItemCompleted domainEvent = notification.DomainEvent;

        WorkItemView? view = await FindAsync(domainEvent.WorkItemId, cancellationToken).ConfigureAwait(false);

        if (view is null)
        {
            return;
        }

        view.Status = nameof(WorkItemStatus.Completed);
        view.CompletedOnUtc = domainEvent.CompletedOnUtc;
        view.Version++;
    }

    private Task<WorkItemView?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        context.WorkItemViews.FirstOrDefaultAsync(view => view.Id == id, cancellationToken);
}
