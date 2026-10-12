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
        string streamId = EventStream.For("TestAggregate", Guid.CreateVersion7());

        await using AsyncServiceScope scope = fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();

        var created = new TestAggregateCreated(Guid.CreateVersion7(), "Persisted");

        await store.AppendToStreamAsync(streamId, "TestAggregate", EventStream.NoStream, [created]);

        IReadOnlyList<IDomainEvent> events = await store.ReadStreamAsync(streamId);

        events.ShouldHaveSingleItem().ShouldBeOfType<TestAggregateCreated>().Name.ShouldBe("Persisted");
    }

    [Fact]
    public async Task AppendToStream_ShouldThrow_WhenTheExpectedVersionIsStale()
    {
        string streamId = EventStream.For("TestAggregate", Guid.CreateVersion7());
        Guid aggregateId = Guid.CreateVersion7();

        await using AsyncServiceScope first = fixture.CreateScope();
        var firstStore = first.ServiceProvider.GetRequiredService<IEventStore>();

        await firstStore.AppendToStreamAsync(
            streamId,
            "TestAggregate",
            EventStream.NoStream,
            [new TestAggregateCreated(aggregateId, "First")]);

        await using AsyncServiceScope second = fixture.CreateScope();
        var secondStore = second.ServiceProvider.GetRequiredService<IEventStore>();

        ConcurrencyException exception = await Should.ThrowAsync<ConcurrencyException>(() =>
            secondStore.AppendToStreamAsync(
                streamId,
                "TestAggregate",
                EventStream.NoStream,
                [new TestAggregateRenamed(aggregateId, "Racing writer")]));

        exception.StreamId.ShouldBe(streamId);
        exception.ActualVersion.ShouldBe(0);
    }

    [Fact]
    public async Task StreamExists_ShouldBeFalse_ForAnUnknownStream()
    {
        await using AsyncServiceScope scope = fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();

        bool exists = await store.StreamExistsAsync(EventStream.For("TestAggregate", Guid.CreateVersion7()));

        exists.ShouldBeFalse();
    }
}
