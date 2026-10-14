using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Domain.Incapacidades;
using Halsa.Backend.SharedKernel.Primitives;
using Microsoft.EntityFrameworkCore.Storage;

namespace Halsa.Backend.Application.Abstractions.Incapacidades;

/// <summary>
/// La unidad de trabajo de la carga de incapacidades (SPEC 07). Es el contexto de la ingesta, conectado como
/// <c>halsa_ingesta</c>, que pasa a ser el rol de "escritura mixta": la parte administrativa, el
/// <c>CERTIFICADO_INCAPACIDAD</c> de la historia clínica, el soporte, la idempotencia, la auditoría y el outbox van en
/// una sola transacción. Sobre lo clínico, como siempre, solo inserta.
///
/// Solo la usa <c>Halsa.Backend.Application.Incapacidades</c> (test de arquitectura).
/// </summary>
public interface IIncapacidadesDbContext
{
    void Add(Incapacidad incapacidad);

    void Add(Soporte soporte);

    void Add(HistoriaClinicaDetalle registro);

    /// <summary>Encola el evento en el outbox, dentro de la misma transacción.</summary>
    void Encolar(IIntegrationEvent evento);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct);

    /// <summary>
    /// Serializa las cargas sobre el mismo caso hasta el fin de la transacción abierta: dos prórrogas concurrentes
    /// de la misma cadena no pueden pasar las reglas a la vez.
    /// </summary>
    Task BloquearCasoAsync(CasoMedicoId casoId, CancellationToken ct);

    /// <summary>El caso, si el actor lo ve (por tenant o por remisión vigente). Pasa por la RLS de <c>casos_medicos</c>.</summary>
    Task<CasoVisible?> CasoVisibleAsync(CasoMedicoId casoId, CancellationToken ct);

    /// <summary>Todas las incapacidades del caso que el actor ve, para aplicar las reglas.</summary>
    Task<IReadOnlyList<Incapacidad>> IncapacidadesDelCasoAsync(CasoMedicoId casoId, CancellationToken ct);

    /// <summary>
    /// Guarda lo agregado. El certificado repetido de la misma IPS y la RLS que rechaza una fila (la remisión se
    /// revocó en medio) vuelven como resultado, no como excepción.
    /// </summary>
    Task<ResultadoGuardadoIncapacidad> GuardarAsync(CancellationToken ct);
}

public enum ResultadoGuardadoIncapacidad
{
    Guardado,

    /// <summary>Ya existe una incapacidad con el mismo <c>(tenant, REPS, numeroCertificado)</c>.</summary>
    CertificadoDuplicado,

    /// <summary>La RLS rechazó una fila: la remisión se revocó entre la lectura del caso y el insert.</summary>
    FueraDeAlcance
}
