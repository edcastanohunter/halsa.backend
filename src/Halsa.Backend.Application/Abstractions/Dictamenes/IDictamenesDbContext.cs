using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Domain.Dictamenes;
using Microsoft.EntityFrameworkCore.Storage;

namespace Halsa.Backend.Application.Abstractions.Dictamenes;

/// <summary>
/// La unidad de trabajo del registro del dictamen PCL (SPEC 08). Es el contexto de la ingesta, conectado como
/// <c>halsa_ingesta</c> (escritura mixta): el registro <c>DICTAMEN_PCL</c> de la HC, <c>atel.dictamenes_pcl</c>, la
/// calificación del caso y la auditoría van en una sola transacción. Sobre <c>casos_medicos</c> el rol solo actualiza
/// <c>estado</c> y <c>calificacion_*</c>, y sobre <c>dictamenes_pcl</c> solo <c>corregido_por</c>.
///
/// Solo la usa <c>Halsa.Backend.Application.Dictamenes</c> (test de arquitectura).
/// </summary>
public interface IDictamenesDbContext
{
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct);

    /// <summary>Serializa los registros sobre el mismo caso hasta el fin de la transacción abierta.</summary>
    Task BloquearCasoAsync(CasoMedicoId casoId, CancellationToken ct);

    /// <summary>El caso, rastreado y sin sus colecciones, si el actor lo ve (RLS de <c>casos_medicos</c>).</summary>
    Task<CasoMedico?> CasoAsync(CasoMedicoId casoId, CancellationToken ct);

    /// <summary>El dictamen vigente del caso, rastreado, o null.</summary>
    Task<DictamenPcl?> VigenteAsync(CasoMedicoId casoId, CancellationToken ct);

    void Add(DictamenPcl dictamen);

    void Add(HistoriaClinicaDetalle registro);

    Task GuardarAsync(CancellationToken ct);
}
