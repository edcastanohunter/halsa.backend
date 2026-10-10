using System.Collections.Frozen;
using System.Reflection;
using Halsa.Backend.SharedKernel.EventSourcing;

namespace Halsa.Backend.Infrastructure.EventSourcing;

/// <summary>
/// Two-way map between the persisted event name and the CLR type, built once at startup from the
/// assemblies that own domain events.
/// </summary>
public sealed class EventTypeRegistry : IEventTypeRegistry
{
    private readonly FrozenDictionary<string, Type> _byName;
    private readonly FrozenDictionary<Type, string> _byType;

    public EventTypeRegistry(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var byName = new Dictionary<string, Type>(StringComparer.Ordinal);
        var byType = new Dictionary<Type, string>();

        foreach (Type type in assemblies.SelectMany(a => a.GetTypes()))
        {
            if (type.IsAbstract || !typeof(IPersistedEvent).IsAssignableFrom(type))
            {
                continue;
            }

            string name = type.GetCustomAttribute<EventNameAttribute>()?.Name
                          ?? throw new InvalidOperationException(
                              $"Persisted event '{type.FullName}' must declare [EventName(\"...\")]. " +
                              "Stored streams must survive a class rename.");

            if (byName.TryGetValue(name, out Type? existing) && existing != type)
            {
                throw new InvalidOperationException(
                    $"Event name '{name}' is claimed by both '{existing.FullName}' and '{type.FullName}'.");
            }

            byName[name] = type;
            byType[type] = name;
        }

        _byName = byName.ToFrozenDictionary(StringComparer.Ordinal);
        _byType = byType.ToFrozenDictionary();
    }

    public string GetName(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return _byType.TryGetValue(eventType, out string? name)
            ? name
            : throw new InvalidOperationException(
                $"'{eventType.FullName}' is not a registered persisted event. " +
                "Implement IPersistedEvent and annotate it with [EventName].");
    }

    public Type Resolve(string eventName) =>
        _byName.TryGetValue(eventName, out Type? type)
            ? type
            : throw new InvalidOperationException(
                $"Stored event '{eventName}' has no matching CLR type. " +
                "Keep an upcaster or the retired type around: history is immutable.");

    public bool TryResolve(string eventName, out Type? eventType) => _byName.TryGetValue(eventName, out eventType);
}
