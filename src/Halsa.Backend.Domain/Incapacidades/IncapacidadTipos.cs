using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Domain.Incapacidades;

/// <summary>Identificador único de una <see cref="Incapacidad"/>. Siempre UUID v7.</summary>
public readonly record struct IncapacidadId(Guid Value)
{
    public static IncapacidadId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>Una incapacidad abre una cadena (<see cref="Inicial"/>) o la continúa (<see cref="Prorroga"/>).</summary>
public enum TipoIncapacidad
{
    Inicial,
    Prorroga
}

public enum ModalidadIncapacidad
{
    Presencial,
    Telemedicina
}

/// <summary>
/// Solo existe <see cref="Registrada"/>: los estados de revisión, aprobación para pago y rechazo pertenecen a la
/// liquidación, fuera de ARQ 01.
/// </summary>
public enum EstadoIncapacidad
{
    Registrada
}

/// <summary>La IPS que expidió el certificado, por su código REPS. El nombre es opcional en el contrato.</summary>
public sealed record IpsEmisora(string Reps, string? Nombre);

/// <summary>
/// El profesional que firmó el certificado. En el backoffice sale del token, que no siempre trae el nombre (claim
/// <c>name</c>): por eso es opcional.
/// </summary>
public sealed record ProfesionalIncapacidad(
    string TipoDocumento,
    string NumeroDocumento,
    string? Nombre,
    string RegistroRethus);

/// <summary>Lo que las reglas necesitan saber del caso.</summary>
public sealed record CasoParaIncapacidad(TenantId TenantId, DateTimeOffset FechaEvento, EstadoCaso Estado);

/// <summary>
/// Los umbrales de las reglas (sección <c>Incapacidades</c>). Están pendientes de validación jurídica antes de
/// producción, como <c>sst.reglas_termino</c>, así que son parámetros y no constantes.
/// </summary>
public sealed record ParametrosIncapacidad
{
    /// <summary>Días máximos entre el fin de una incapacidad y el inicio de su prórroga (D. 1427/2022).</summary>
    public int MaxDiasEntreProrrogas { get; init; } = 30;

    /// <summary>A partir de cuántos días entre el inicio y la carga una incapacidad es extemporánea.</summary>
    public int DiasCargaExtemporanea { get; init; } = 30;

    public static readonly ParametrosIncapacidad PorDefecto = new();
}

/// <summary>Los datos de una incapacidad nueva, ya validados en formato.</summary>
/// <param name="DiagnosticoCategoriaIndice">El índice ciego de la categoría CIE-10 del diagnóstico principal.</param>
/// <param name="SoporteId">El id del PDF en <c>clinico.soportes</c>.</param>
/// <param name="RegistroClinicoId">El id del <c>CERTIFICADO_INCAPACIDAD</c> en la historia clínica.</param>
/// <param name="RegistradaPor">El <c>sub</c> del actor que la carga.</param>
public sealed record DatosIncapacidad(
    IncapacidadId Id,
    CasoMedicoId CasoMedicoId,
    string NumeroCertificado,
    IpsEmisora IpsEmisora,
    TipoIncapacidad Tipo,
    IncapacidadId? ProrrogaDe,
    DateOnly FechaInicio,
    int Dias,
    ModalidadIncapacidad Modalidad,
    ProfesionalIncapacidad Profesional,
    byte[] DiagnosticoCategoriaIndice,
    Guid SoporteId,
    Guid RegistroClinicoId,
    string RegistradaPor,
    string? ClientId);
