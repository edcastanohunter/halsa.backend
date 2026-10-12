namespace Halsa.Backend.Domain.Clinico;

/// <summary>
/// Quién firma el registro. <see cref="RegistroRethus"/> solo puede ser null en un registro
/// <see cref="TipoRegistroClinico.ReporteInicial"/>: el informante de un FURAT no es médico (SPEC 05).
/// </summary>
public sealed record ProfesionalRef(
    string Sub,
    string TipoDocumento,
    string NumeroDocumento,
    string? RegistroRethus);
