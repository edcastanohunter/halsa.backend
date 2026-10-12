using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using Halsa.Backend.Application.Abstractions.Events;
using Halsa.Backend.Infrastructure.EventSourcing;
using Halsa.Backend.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Halsa.Backend.Infrastructure.Outbox;

/// <summary>
/// Entrega los eventos del outbox a los <see cref="IIntegrationEventHandler{TEvent}"/> del propio proceso (SPEC 05).
/// Sin broker en el VPS, el outbox ya garantiza <em>at-least-once</em>: este bus solo despacha.
///
/// Un scope de DI por mensaje, así cada mensaje tiene su propio contexto de tenant y su propio <c>DbContext</c>.
/// Si un handler lanza, la excepción sube al <see cref="OutboxProcessor"/>, que deja el mensaje pendiente con su
/// error y lo reintenta. Un tipo sin handlers solo se registra en el log, como hacía el bus anterior.
/// </summary>
internal sealed partial class InProcessEventBus(IServiceScopeFactory scopeFactory, ILogger<InProcessEventBus> logger)
    : IEventBus
{
    /// <summary>Los eventos de integración de la aplicación, por el nombre con que el outbox los guarda.</summary>
    private static readonly FrozenDictionary<string, Type> Tipos = typeof(Application.DependencyInjection).Assembly
        .GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsAssignableTo(typeof(IIntegrationEvent)))
        .ToFrozenDictionary(type => type.FullName!, StringComparer.Ordinal);

    private static readonly MethodInfo DespacharMethod =
        typeof(InProcessEventBus).GetMethod(nameof(DespacharAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly ConcurrentDictionary<Type, Func<IServiceProvider, object, CancellationToken, Task>> Despachadores = new();

    public async Task PublishAsync(string eventType, string payload, CancellationToken cancellationToken = default)
    {
        Published(logger, eventType, payload);

        if (!Tipos.TryGetValue(eventType, out Type? tipo))
        {
            SinContrato(logger, eventType);
            return;
        }

        object evento = JsonSerializer.Deserialize(payload, tipo, EventSerializer.Options)
            ?? throw new JsonException($"El mensaje {eventType} del outbox está vacío.");

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        await Despachadores
            .GetOrAdd(tipo, CrearDespachador)
            .Invoke(scope.ServiceProvider, evento, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Func<IServiceProvider, object, CancellationToken, Task> CrearDespachador(Type tipo) =>
        DespacharMethod.MakeGenericMethod(tipo).CreateDelegate<Func<IServiceProvider, object, CancellationToken, Task>>();

    private static async Task DespacharAsync<TEvent>(IServiceProvider services, object evento, CancellationToken ct)
        where TEvent : IIntegrationEvent
    {
        foreach (IIntegrationEventHandler<TEvent> handler in services.GetServices<IIntegrationEventHandler<TEvent>>())
        {
            await handler.HandleAsync((TEvent)evento, ct).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information,
        Message = "Integration event {EventType} published: {Payload}")]
    private static partial void Published(ILogger logger, string eventType, string payload);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning,
        Message = "Integration event {EventType} has no contract in this process; nothing to dispatch")]
    private static partial void SinContrato(ILogger logger, string eventType);
}
