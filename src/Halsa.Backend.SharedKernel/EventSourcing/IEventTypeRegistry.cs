using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.SharedKernel.EventSourcing;

/// <summary>
/// Maps a stable, persisted event name to a CLR type. Never persist assembly-qualified names:
/// renaming a class must not invalidate years of history.
/// </summary>
public interface IEventTypeRegistry
{
    string GetName(Type eventType);

    Type Resolve(string eventName);

    bool TryResolve(string eventName, out Type? eventType);
}

/// <summary>
/// Opt-in stable name for a persisted event. Apply it to every event that reaches the store.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class EventNameAttribute : Attribute
{
    public EventNameAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        Name = name;
    }

    public string Name { get; }
}

/// <summary>Marker for events that are safe to persist in the store.</summary>
public interface IPersistedEvent : IDomainEvent;
