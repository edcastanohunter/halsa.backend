namespace Halsa.Backend.Domain.Casos;

/// <summary>
/// Documento de identidad de un trabajador o empleador.
/// Tipos válidos: CC, CE, TI, PA, PE, PT, NIT.
/// </summary>
public sealed record Identificacion(string Tipo, string Numero);
