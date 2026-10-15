using Halsa.Backend.Application.Abstractions.Models;
using Halsa.Backend.Application.Webhooks;
using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.Domain.Webhooks;
using Halsa.Backend.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Infrastructure.Webhooks;

/// <summary>
/// Sobre el <see cref="ApplicationDbContext"/> del request (<c>halsa_app</c>): la RLS y el filtro del contexto limitan
/// todo al emisor de la sesión, así que una suscripción de otro emisor no existe para él.
/// </summary>
internal sealed class SuscripcionesWebhookRepository(ApplicationDbContext db) : ISuscripcionesWebhookRepository
{
    public async Task<IReadOnlyList<SuscripcionWebhook>> ListarAsync(CancellationToken ct) =>
        await db.SuscripcionesWebhook
            .AsNoTracking()
            .Where(s => s.Estado != EstadoSuscripcion.Eliminada)
            .OrderBy(s => s.CreadaEn)
            .ToListAsync(ct);

    public Task<SuscripcionWebhook?> GetAsync(SuscripcionWebhookId id, CancellationToken ct) =>
        db.SuscripcionesWebhook.FirstOrDefaultAsync(s => s.Id == id && s.Estado != EstadoSuscripcion.Eliminada, ct);

    public void Agregar(SuscripcionWebhook suscripcion) => db.SuscripcionesWebhook.Add(suscripcion);

    public async Task<PagedList<EntregaWebhookResponse>> EntregasAsync(
        SuscripcionWebhookId id, EstadoEntrega? estado, int page, int pageSize, CancellationToken ct)
    {
        IQueryable<EntregaWebhook> q = db.EntregasWebhook.AsNoTracking().Where(e => e.SuscripcionId == id);
        if (estado is { } filtro)
        {
            q = q.Where(e => e.Estado == filtro);
        }

        int total = await q.CountAsync(ct);

        // Without the payload: the history is for operations, and the body is the receiver's business.
        List<EntregaWebhookResponse> pagina = await q
            .OrderByDescending(e => e.PrimerIntentoEn)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new EntregaWebhookResponse(
                e.Id.Value, e.EventoId, e.Tipo, CodigosDictamen.Codigo(e.Estado), e.Intentos, e.PrimerIntentoEn,
                e.ProximoIntentoEn, e.UltimoStatus, e.UltimoError, e.EntregadaEn))
            .ToListAsync(ct);

        return new PagedList<EntregaWebhookResponse>(pagina, page, pageSize, total);
    }

    public Task<EntregaWebhook?> EntregaAsync(SuscripcionWebhookId suscripcion, EntregaWebhookId id, CancellationToken ct) =>
        db.EntregasWebhook.FirstOrDefaultAsync(e => e.Id == id && e.SuscripcionId == suscripcion, ct);

    public Task DescartarPendientesAsync(SuscripcionWebhookId id, CancellationToken ct) =>
        db.EntregasWebhook
            .Where(e => e.SuscripcionId == id && e.Estado == EstadoEntrega.Pendiente)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.Estado, EstadoEntrega.Descartada), ct);
}
