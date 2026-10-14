using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Incapacidades;

/// <summary>
/// Registra una incapacidad con su soporte (SPEC 07). El endpoint ya comprobó la política, las partes, los tamaños y
/// la firma <c>%PDF-</c>; el comando hace lo demás: la idempotencia, el formato de <c>datos</c>, el antivirus, el
/// archivo cifrado y la transacción.
/// </summary>
/// <param name="IdempotencyKey">La cabecera tal cual llegó.</param>
/// <param name="Datos">La parte <c>datos</c>, cruda: con el soporte, forma la huella de la idempotencia.</param>
/// <param name="Soporte">El PDF, ya acotado a 10 MB.</param>
/// <param name="ProfesionalDelToken">
/// En el backoffice, quien firma según el token (<c>IProfesionalActual</c>); en la integración, <c>null</c>, y el
/// profesional sale del cuerpo.
/// </param>
/// <param name="ClientId">El <c>azp</c> del token.</param>
public sealed record RegistrarIncapacidadCommand(
    CasoMedicoId CasoId,
    string? IdempotencyKey,
    ReadOnlyMemory<byte> Datos,
    ReadOnlyMemory<byte> Soporte,
    ProfesionalDelToken? ProfesionalDelToken,
    string? ClientId) : ICommand<IncapacidadResponse>;

/// <summary>El profesional del backoffice: su referencia firmada por el token y, si el token lo trae, su nombre.</summary>
public sealed record ProfesionalDelToken(ProfesionalRef Ref, string? Nombre);

/// <summary>Los errores de la carga que no son reglas del dominio.</summary>
public static class IncapacidadCargaErrors
{
    public static readonly Error ActorNoAutorizado =
        Error.Forbidden("Incapacidades.ActorNoAutorizado",
            "Solo una persona de una ARL o de una IPS, o la integración de una IPS, pueden cargar incapacidades.");

    public static readonly Error CasoNoEncontrado =
        Error.NotFound("Incapacidades.CasoNoEncontrado",
            "El caso no existe o no es visible para el actor actual.");

    public static readonly Error CertificadoDuplicado =
        Error.Conflict("Incapacidades.CertificadoDuplicado",
            "La IPS ya cargó un certificado con ese número para esta ARL.");

    public static readonly Error SoporteInfectado =
        Error.Unprocessable("Archivos.SoporteInfectado",
            "El antivirus encontró una amenaza en el soporte. El archivo no se guardó.");

    public static readonly Error AntivirusNoDisponible =
        Error.Unavailable("Archivos.AntivirusNoDisponible",
            "El antivirus no está disponible y el soporte no se puede analizar. Reintente más tarde.");

    public static readonly Error NoEsPdf =
        Error.UnsupportedMediaType("Archivos.NoEsPdf", "El soporte no es un PDF: no empieza por %PDF-.");

    public static readonly Error SoporteDemasiadoGrande =
        Error.PayloadTooLarge("Archivos.SoporteDemasiadoGrande",
            "El soporte supera los 10 MB, o el request supera los 12 MB.");

    public static readonly Error PartesInvalidas =
        Error.Problem("Incapacidades.PartesInvalidas",
            "El cuerpo debe ser multipart/form-data con exactamente dos partes: datos (application/json) y soporte (application/pdf).");
}
