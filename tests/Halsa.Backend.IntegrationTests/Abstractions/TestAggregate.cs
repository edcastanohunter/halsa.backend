using Halsa.Backend.SharedKernel.EventSourcing;
using Halsa.Backend.SharedKernel.Primitives;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.IntegrationTests.Abstractions;

// ── Events ────────────────────────────────────────────────────────────────────

[EventName("TestAggregate.Created")]
public sealed record TestAggregateCreated(Guid AggregateId, string Name) : DomainEvent, IPersistedEvent;

[EventName("TestAggregate.Renamed")]
public sealed record TestAggregateRenamed(Guid AggregateId, string NewName) : DomainEvent, IPersistedEvent;

// ── Value object ──────────────────────────────────────────────────────────────

public readonly record struct TestAggregateId(Guid Value)
{
    public static TestAggregateId New() => new(Guid.CreateVersion7());
}

// ── Aggregate ─────────────────────────────────────────────────────────────────

/// <summary>
/// Minimal event-sourced aggregate that lives only in the integration-test assembly.
/// Its sole purpose is to exercise <see cref="IEventStore"/> and
/// <see cref="IEventSourcedRepository{TAggregate,TId}"/> after WorkItems are removed.
/// </summary>
public sealed class TestAggregate : EventSourcedAggregateRoot<TestAggregateId>
{
    // Private parameterless constructor required by EventSourcedRepository.CreateEmpty().
    private TestAggregate() { }

    public string Name { get; private set; } = string.Empty;

    public static Result<TestAggregate> Create(TestAggregateId id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));

        var agg = new TestAggregate();
        agg.RaiseAndApply(new TestAggregateCreated(id.Value, name));
        return Result.Success(agg);
    }

    public Result Rename(string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName, nameof(newName));

        RaiseAndApply(new TestAggregateRenamed(Id.Value, newName));
        return Result.Success();
    }

    protected override void Apply(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case TestAggregateCreated e:
                // Set the Id on first event — the aggregate loads from an empty instance
                // (via Activator.CreateInstance) when replaying, so Id must be set here.
                Id = new TestAggregateId(e.AggregateId);
                Name = e.Name;
                break;

            case TestAggregateRenamed e:
                Name = e.NewName;
                break;
        }
    }
}
