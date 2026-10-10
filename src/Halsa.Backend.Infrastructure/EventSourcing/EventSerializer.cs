using System.Text.Json;
using System.Text.Json.Serialization;
using Halsa.Backend.SharedKernel.EventSourcing;
using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Infrastructure.EventSourcing;

/// <summary>
/// JSON codec for stored events. The options are pinned deliberately: a serializer setting change
/// must never make years of history unreadable.
/// </summary>
public sealed class EventSerializer(IEventTypeRegistry registry)
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.Strict,
        WriteIndented = false
    };

    public string Serialize(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), Options);
    }

    public IDomainEvent Deserialize(string eventName, string payload)
    {
        Type eventType = registry.Resolve(eventName);

        return JsonSerializer.Deserialize(payload, eventType, Options) as IDomainEvent
               ?? throw new InvalidOperationException($"Stored event '{eventName}' could not be deserialised.");
    }

    public string GetName(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        return registry.GetName(domainEvent.GetType());
    }
}
