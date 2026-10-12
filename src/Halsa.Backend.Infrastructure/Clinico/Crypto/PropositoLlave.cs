namespace Halsa.Backend.Infrastructure.Clinico.Crypto;

/// <summary>
/// Para qué sirve una DEK. Cada tenant tiene una por propósito, así que comprometer el índice ciego
/// no compromete el contenido. La SPEC 05 añade <c>INTEGRACION</c> y la SPEC 07 <c>SOPORTES</c>.
/// </summary>
internal static class PropositoLlave
{
    public const string Contenido = "CONTENIDO";

    public const string Indice = "INDICE";
}
