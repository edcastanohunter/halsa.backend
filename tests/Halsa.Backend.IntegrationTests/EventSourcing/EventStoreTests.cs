using Halsa.Backend.Domain.WorkItems;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.SharedKernel.EventSourcing;
using Halsa.Backend.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace Halsa.Backend.IntegrationTests.EventSourcing;

[Collection(nameof(DatabaseCollection))]
public sealed class EventStoreTests(DatabaseFixture fixture)
{
    [Fact]
    public async Task AppendToStream_ShouldRoundTripEvents()
    {
        string streamId = EventStream.For("WorkItem", Guid.CreateVersion7());

        await using AsyncServiceScope scope = fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();

        var created = new WorkItemCreated(Guid.CreateVersion7(), "Persisted", "payload", DateTime.UtcNow);

        await store.AppendToStreamAsync(streamId, "WorkItem", EventStream.NoStream, [created]);

        IReadOnlyList<IDomainEvent> events = await store.ReadStreamAsync(streamId);

        events.ShouldHaveSingleItem().ShouldBeOfType<WorkItemCreated>().Title.ShouldBe("Persisted");
    }

    [Fact]
    public async Task AppendToStream_ShouldThrow_WhenTheExpectedVersionIsStale()
    {
        string streamId = EventStream.For("WorkItem", Guid.CreateVersion7());
        Guid workItemId = Guid.CreateVersion7();

        await using AsyncServiceScope first = fixture.CreateScope();
        var firstStore = first.ServiceProvider.GetRequiredService<IEventStore>();

        await firstStore.AppendToStreamAsync(
            streamId,
            "WorkItem",
            EventStream.NoStream,
            [new WorkItemCreated(workItemId, "First", null, DateTime.UtcNow)]);

        await using AsyncServiceScope second = fixture.CreateScope();
        var secondStore = second.ServiceProvider.GetRequiredService<IEventStore>();

        ConcurrencyException exception = await Should.ThrowAsync<ConcurrencyException>(() =>
            secondStore.AppendToStreamAsync(
                streamId,
                "WorkItem",
                EventStream.NoStream,
                [new WorkItemRenamed(workItemId, "Racing writer")]));

        exception.StreamId.ShouldBe(streamId);
        exception.ActualVersion.ShouldBe(0);
    }

    [Fact]
    public async Task StreamExists_ShouldBeFalse_ForAnUnknownStream()
    {
        await using AsyncServiceScope scope = fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();

        bool exists = await store.StreamExistsAsync(EventStream.For("WorkItem", Guid.CreateVersion7()));

        exists.ShouldBeFalse();
    }
}
