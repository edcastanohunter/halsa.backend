namespace Halsa.Backend.Infrastructure.Clinico.Crypto;

/// <summary>
/// Para qué sirve una DEK. Cada tenant tiene una por propósito, así que comprometer el índice ciego
/// no compromete el contenido.
/// </summary>
internal static class PropositoLlave
{
    public const string Contenido = "CONTENIDO";

    public const string Indice = "INDICE";

    /// <summary>El mensaje crudo de una integración (SPEC 05). Se rota o revoca aparte de <see cref="Contenido"/>.</summary>
    public const string Integracion = "INTEGRACION";

    /// <summary>Los archivos soporte (SPEC 07): un PDF de incapacidad cifrado en el volumen de archivos.</summary>
    public const string Soportes = "SOPORTES";
}
