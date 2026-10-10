using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.WorkItems.ReadModels;
using Halsa.Backend.Domain.WorkItems;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.SharedKernel.EventSourcing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Halsa.Backend.IntegrationTests.EventSourcing;

[Collection(nameof(DatabaseCollection))]
public sealed class WorkItemRepositoryTests(DatabaseFixture fixture)
{
    [Fact]
    public async Task Save_ShouldAppendEventsAndProjectTheReadModel()
    {
        WorkItemId id = WorkItemId.New();

        await using (AsyncServiceScope scope = fixture.CreateScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<IEventSourcedRepository<WorkItem, WorkItemId>>();

            WorkItem workItem = WorkItem.Create(id, "Projected", "desc", DateTime.UtcNow).Value;

            await repository.SaveAsync(workItem);
        }

        await using (AsyncServiceScope scope = fixture.CreateScope())
        {
            var readDbContext = scope.ServiceProvider.GetRequiredService<IReadDbContext>();

            WorkItemView? view = await readDbContext.WorkItems
                .FirstOrDefaultAsync(item => item.Id == id.Value);

            view.ShouldNotBeNull();
            view.Title.ShouldBe("Projected");
            view.Status.ShouldBe(nameof(WorkItemStatus.Open));
        }
    }

    [Fact]
    public async Task Load_ShouldReplayTheAggregate()
    {
        WorkItemId id = WorkItemId.New();

        await using (AsyncServiceScope scope = fixture.CreateScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<IEventSourcedRepository<WorkItem, WorkItemId>>();

            WorkItem workItem = WorkItem.Create(id, "Replay me", null, DateTime.UtcNow).Value;
            await repository.SaveAsync(workItem);
        }

        await using (AsyncServiceScope scope = fixture.CreateScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<IEventSourcedRepository<WorkItem, WorkItemId>>();

            WorkItem? loaded = await repository.LoadAsync(id);

            loaded.ShouldNotBeNull();
            loaded.Title.ShouldBe("Replay me");
            loaded.Version.ShouldBe(0);
            loaded.UncommittedEvents.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Complete_ShouldWriteAnOutboxMessage()
    {
        WorkItemId id = WorkItemId.New();

        await using (AsyncServiceScope scope = fixture.CreateScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<IEventSourcedRepository<WorkItem, WorkItemId>>();

            WorkItem workItem = WorkItem.Create(id, "Complete me", null, DateTime.UtcNow).Value;
            await repository.SaveAsync(workItem);

            WorkItem? reloaded = await repository.LoadAsync(id);
            reloaded!.Complete(DateTime.UtcNow).IsSuccess.ShouldBeTrue();
            await repository.SaveAsync(reloaded);
        }

        await using (AsyncServiceScope scope = fixture.CreateScope())
        {
            var context = scope.ServiceProvider
                .GetRequiredService<Infrastructure.Database.ApplicationDbContext>();

            bool enqueued = await context.Set<Infrastructure.Outbox.OutboxMessage>()
                .AnyAsync(message => message.Type.Contains("WorkItemCompletedIntegrationEvent"));

            enqueued.ShouldBeTrue();
        }
    }
}
