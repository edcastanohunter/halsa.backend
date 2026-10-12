using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.SharedKernel.EventSourcing;
using Microsoft.Extensions.DependencyInjection;

namespace Halsa.Backend.IntegrationTests.EventSourcing;

/// <summary>
/// Verifies that <see cref="IEventSourcedRepository{TAggregate,TId}"/> correctly persists and
/// replays a <see cref="TestAggregate"/> through the event store.
///
/// Renamed from <c>WorkItemRepositoryTests</c> in SPEC 02; the aggregate changed but the
/// infrastructure under test is the same.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class TestAggregateRepositoryTests(DatabaseFixture fixture)
{
    [Fact]
    public async Task Save_ShouldAppendEventsAndReplayCorrectly()
    {
        TestAggregateId id = TestAggregateId.New();

        // ── Write ─────────────────────────────────────────────────────────────
        await using (AsyncServiceScope scope = fixture.CreateScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<IEventSourcedRepository<TestAggregate, TestAggregateId>>();

            TestAggregate aggregate = TestAggregate.Create(id, "Initial").Value;

            await repository.SaveAsync(aggregate);
        }

        // ── Read ──────────────────────────────────────────────────────────────
        await using (AsyncServiceScope scope = fixture.CreateScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<IEventSourcedRepository<TestAggregate, TestAggregateId>>();

            TestAggregate? loaded = await repository.LoadAsync(id);

            loaded.ShouldNotBeNull();
            loaded.Name.ShouldBe("Initial");
            loaded.Id.ShouldBe(id);
        }
    }

    [Fact]
    public async Task Save_ShouldAppendSubsequentEvents()
    {
        TestAggregateId id = TestAggregateId.New();

        // ── Create ────────────────────────────────────────────────────────────
        await using (AsyncServiceScope scope = fixture.CreateScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<IEventSourcedRepository<TestAggregate, TestAggregateId>>();

            TestAggregate aggregate = TestAggregate.Create(id, "Before Rename").Value;
            await repository.SaveAsync(aggregate);
        }

        // ── Mutate ────────────────────────────────────────────────────────────
        await using (AsyncServiceScope scope = fixture.CreateScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<IEventSourcedRepository<TestAggregate, TestAggregateId>>();

            TestAggregate aggregate = (await repository.LoadAsync(id))!;
            aggregate.Rename("After Rename").IsSuccess.ShouldBeTrue();
            await repository.SaveAsync(aggregate);
        }

        // ── Verify ────────────────────────────────────────────────────────────
        await using (AsyncServiceScope scope = fixture.CreateScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<IEventSourcedRepository<TestAggregate, TestAggregateId>>();

            TestAggregate? loaded = await repository.LoadAsync(id);

            loaded.ShouldNotBeNull();
            loaded.Name.ShouldBe("After Rename");
        }
    }
}
