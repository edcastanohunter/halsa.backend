using System.Net;
using Halsa.Backend.Infrastructure.Auditoria;

namespace Halsa.Backend.Infrastructure.Database.Auditoria;

/// <summary>
/// Una fila de <c>auditoria.accesos</c>. Se construye con <see cref="AccesoAuditadoFactory"/>, que
/// calcula el hash, y la base no deja modificarla después.
/// Los enums se guardan como códigos (<c>LEER</c>, <c>NO_ENCONTRADO</c>…) para que la tabla se lea sin la aplicación.
/// </summary>
internal sealed class AccesoAuditado
{
    public Guid Id { get; init; }

    /// <summary>Truncado a microsegundos: lo que guarda <c>timestamptz</c>, así el hash se puede recalcular.</summary>
    public DateTimeOffset OcurridoEn { get; init; }

    /// <summary>Tenant del recurso, no del actor. <c>null</c> si no se conoce.</summary>
    public string? TenantId { get; init; }

    public string ActorId { get; init; } = string.Empty;

    public string ActorTipo { get; init; } = string.Empty;

    public string? ActorTenantId { get; init; }

    public string? ActorIpsId { get; init; }

    public string[] ActorRoles { get; init; } = [];

    public string? ClientId { get; init; }

    public IPAddress? Ip { get; init; }

    public string? UserAgent { get; init; }

    public string CorrelationId { get; init; } = string.Empty;

    public string Accion { get; init; } = string.Empty;

    public string Resultado { get; init; } = string.Empty;

    public string RecursoTipo { get; init; } = string.Empty;

    public string? RecursoId { get; init; }

    public Guid? CasoMedicoId { get; init; }

    /// <summary>Lo llena la SPEC 08 (step-up y break-the-glass).</summary>
    public string? Motivo { get; init; }

    public string? Acr { get; init; }

    /// <summary>SHA-256 de la forma canónica JCS de todas las columnas anteriores.</summary>
    public byte[] HashRegistro { get; private set; } = [];

    /// <summary>Calcula <see cref="HashRegistro"/> sobre el resto de columnas. Va después de llenarlas todas.</summary>
    public void CalcularHash() => HashRegistro = CanonicalAuditHasher.Hash(this);
}
