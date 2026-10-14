using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Incapacidades;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Clinico.Queries.GetSoporte;

/// <summary>
/// El soporte (PDF) de una incapacidad del caso (SPEC 08): solo la metadata para abrirlo, leída de
/// <c>clinico.soportes</c> con <c>halsa_clinico</c>. La lectura se audita (<c>LEER / SOPORTE</c>) antes de abrir el
/// archivo: el endpoint lo abre después con <see cref="SoporteArchivo.AbrirVerificadoAsync"/>.
/// </summary>
public sealed record GetSoporteQuery(CasoMedicoId CasoId, IncapacidadId IncapacidadId)
    : IQuery<SoporteDescargable>, IClinicalQuery
{
    public AccionAuditoria Accion => AccionAuditoria.Leer;

    public ObjetivoAuditoria Objetivo => new(RecursoAuditoria.Soporte, IncapacidadId.Value.ToString(), CasoId.Value);
}

/// <summary>Lo necesario para abrir el archivo y nombrar la descarga. Nunca sale tal cual por la API.</summary>
public sealed record SoporteDescargable(
    TenantId TenantId,
    Guid SoporteId,
    string KeyId,
    byte[] Sha256Claro,
    string NumeroCertificado);

public static class SoporteErrors
{
    public static readonly Error NoEncontrado =
        Error.NotFound("Clinico.SoporteNoEncontrado",
            "La incapacidad o su soporte no existen en este caso, o no son visibles para el actor actual.");

    /// <summary>El archivo falla el tag de GCM, el SHA-256 o no está: nunca se entrega un byte de él.</summary>
    public static readonly Error Corrupto =
        Error.Failure("Archivos.Corrupto",
            "El archivo soporte no pasó la verificación de integridad y no se entregó.");
}
