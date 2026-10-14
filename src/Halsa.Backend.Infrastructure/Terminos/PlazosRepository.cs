using Halsa.Backend.Application.Plazos;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Infrastructure.Terminos;

/// <summary>Los plazos rastreados por <see cref="ApplicationDbContext"/> (<c>halsa_app</c>); los confirma su unidad de trabajo.</summary>
internal sealed class PlazosRepository(ApplicationDbContext db) : IPlazosRepository
{
    public Task<List<Plazo>> DelCasoAsync(CasoMedicoId casoId, CancellationToken ct) =>
        db.Plazos.Where(p => p.CasoMedicoId == casoId).ToListAsync(ct);

    public Task<Plazo?> GetAsync(CasoMedicoId casoId, PlazoId id, CancellationToken ct) =>
        db.Plazos.FirstOrDefaultAsync(p => p.Id == id && p.CasoMedicoId == casoId, ct);

    public void Agregar(Plazo plazo) => db.Plazos.Add(plazo);
}

/// <summary>Las reglas y los festivos, leídos como <c>halsa_app</c> (solo <c>SELECT</c>).</summary>
internal sealed class CatalogoTerminos(ApplicationDbContext db) : ICatalogoTerminos
{
    public async Task<ReglaTermino> ReglaVigenteAsync(string codigo, DateTimeOffset iniciaEn, CancellationToken ct)
    {
        List<ReglaTermino> versiones = await db.ReglasTermino
            .AsNoTracking()
            .Where(r => r.Codigo == codigo)
            .OrderByDescending(r => r.VigenteDesde)
            .ToListAsync(ct);

        return versiones.FirstOrDefault(r => r.VigenteEn(iniciaEn))
            ?? throw new InvalidOperationException($"No hay una regla {codigo} vigente en {iniciaEn:O}.");
    }

    public async Task<CalculadoraTerminos> CalculadoraAsync(CancellationToken ct) =>
        new(await db.Festivos.AsNoTracking().Select(f => f.Fecha).ToListAsync(ct));
}
