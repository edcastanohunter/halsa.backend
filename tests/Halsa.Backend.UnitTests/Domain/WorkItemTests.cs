using Halsa.Backend.Domain.WorkItems;
using Halsa.Backend.SharedKernel.EventSourcing;
using Halsa.Backend.SharedKernel.Primitives;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.UnitTests.Domain;

public sealed class WorkItemTests
{
    private static readonly DateTime Now = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_ShouldRaiseCreatedEvent()
    {
        WorkItemId id = WorkItemId.New();

        Result<WorkItem> result = WorkItem.Create(id, "Ship the template", "with tests", Now);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Id.ShouldBe(id);
        result.Value.Title.ShouldBe("Ship the template");
        result.Value.Status.ShouldBe(WorkItemStatus.Open);
        result.Value.UncommittedEvents.ShouldHaveSingleItem().ShouldBeOfType<WorkItemCreated>();
    }

    [Fact]
    public void Create_ShouldStartAtVersionZero()
    {
        WorkItem workItem = CreateWorkItem();

        workItem.Version.ShouldBe(0);
        workItem.OriginalVersion.ShouldBe(EventStream.NoStream);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ShouldFail_WhenTitleIsBlank(string title)
    {
        Result<WorkItem> result = WorkItem.Create(WorkItemId.New(), title, null, Now);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(WorkItemErrors.TitleRequired);
    }

    [Fact]
    public void Create_ShouldFail_WhenTitleIsTooLong()
    {
        Result<WorkItem> result = WorkItem.Create(
            WorkItemId.New(),
            new string('a', WorkItem.TitleMaxLength + 1),
            null,
            Now);

        result.Error.ShouldBe(WorkItemErrors.TitleTooLong);
    }

    [Fact]
    public void Rename_ShouldBeIdempotent_WhenTitleIsUnchanged()
    {
        WorkItem workItem = CreateWorkItem();
        workItem.ClearDomainEvents();

        Result result = workItem.Rename("Ship the template");

        result.IsSuccess.ShouldBeTrue();
        workItem.UncommittedEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Rename_ShouldRaiseRenamedEvent()
    {
        WorkItem workItem = CreateWorkItem();

        workItem.Rename("Ship it twice").IsSuccess.ShouldBeTrue();

        workItem.Title.ShouldBe("Ship it twice");
        workItem.UncommittedEvents.OfType<WorkItemRenamed>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Complete_ShouldFail_WhenAlreadyCompleted()
    {
        WorkItem workItem = CreateWorkItem();
        workItem.Complete(Now).IsSuccess.ShouldBeTrue();

        Result second = workItem.Complete(Now);

        second.IsFailure.ShouldBeTrue();
        second.Error.Type.ShouldBe(ErrorType.Conflict);
    }

    [Fact]
    public void LoadFromHistory_ShouldRebuildTheSameState()
    {
        WorkItem original = CreateWorkItem();
        original.Rename("Renamed");
        original.Complete(Now.AddHours(2));

        IReadOnlyList<IDomainEvent> history = original.UncommittedEvents;

        WorkItem replayed = Replay(history);

        replayed.Id.ShouldBe(original.Id);
        replayed.Title.ShouldBe(original.Title);
        replayed.Status.ShouldBe(original.Status);
        replayed.CompletedOnUtc.ShouldBe(original.CompletedOnUtc);
        replayed.Version.ShouldBe(original.Version);
        replayed.UncommittedEvents.ShouldBeEmpty();
    }

    private static WorkItem CreateWorkItem() =>
        WorkItem.Create(WorkItemId.New(), "Ship the template", "with tests", Now).Value;

    private static WorkItem Replay(IEnumerable<IDomainEvent> history)
    {
        var workItem = (WorkItem)Activator.CreateInstance(typeof(WorkItem), nonPublic: true)!;
        workItem.LoadFromHistory(history);
        return workItem;
    }
}
