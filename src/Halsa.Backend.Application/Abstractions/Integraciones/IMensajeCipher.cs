using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Abstractions.Integraciones;

/// <summary>
/// Cifrado del mensaje crudo de una integración (<c>integracion.mensajes_entrantes</c>, SPEC 05):
/// AES-256-GCM con la DEK <c>INTEGRACION</c> del tenant, propia para poder rotarla o revocarla aparte de la
/// del contenido clínico, y AAD <c>"{tenant}|{solicitudId}|mensaje"</c>.
/// </summary>
public interface IMensajeCipher
{
    Task<CampoCifrado> CifrarAsync(TenantId tenant, Guid solicitudId, ReadOnlyMemory<byte> cuerpo, CancellationToken ct);

    /// <summary>
    /// Lanza una <see cref="System.Security.Cryptography.CryptographicException"/> si el mensaje no es de ese
    /// tenant y esa solicitud, o si fue alterado.
    /// </summary>
    Task<byte[]> DescifrarAsync(TenantId tenant, Guid solicitudId, CampoCifrado cifrado, CancellationToken ct);
}
