using Halsa.Backend.SharedKernel.Primitives;

namespace Halsa.Backend.Application.Abstractions.Events;

/// <summary>
/// Reacciona a un evento de integración que entrega el outbox (SPEC 05). Corre fuera de cualquier request, en
/// un scope de DI propio por mensaje, así que no hay actor: el handler entra en <c>ISystemTenantScope</c> para el
/// tenant del evento antes de tocar datos.
///
/// La entrega es <em>at-least-once</em>: el mismo evento puede llegar dos veces y el handler debe tolerarlo. Si
/// lanza, el mensaje queda pendiente y se reintenta hasta <c>Outbox:MaxAttempts</c>.
/// </summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent evento, CancellationToken cancellationToken);
}
