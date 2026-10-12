namespace Halsa.Backend.Domain.Clinico;

/// <summary>Tipo de registro de la historia clínica ocupacional.</summary>
public enum TipoRegistroClinico
{
    ReporteInicial,
    ConceptoMedico,
    Evolucion,
    ConceptoRehabilitacion,
    DictamenPcl,

    /// <summary>Lo usa la SPEC 07.</summary>
    CertificadoIncapacidad
}
