using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Abstractions.Clinico;

/// <summary>
/// Cifrado de campo del detalle clínico (AES-256-GCM con DEK por tenant). El handler cifra de forma
/// explícita: un <c>ValueConverter</c> no tiene el tenant ni el id de la fila que exige la AAD.
/// </summary>
public interface IClinicalCipher
{
    /// <summary>Cifra <paramref name="claro"/> con AAD <c>"{tenant}|{registroId}|{campo}"</c>.</summary>
    Task<CampoCifrado> CifrarAsync(
        TenantId tenant, Guid registroId, string campo, ReadOnlyMemory<byte> claro, CancellationToken ct);

    /// <summary>
    /// Descifra un campo. Lanza una <see cref="System.Security.Cryptography.CryptographicException"/>
    /// si el campo no pertenece a ese tenant, registro y campo, o si fue alterado.
    /// </summary>
    Task<byte[]> DescifrarAsync(
        TenantId tenant, Guid registroId, string campo, CampoCifrado cifrado, CancellationToken ct);

    /// <summary>HMAC determinista del código con la DEK de índice del tenant: permite buscar sin descifrar.</summary>
    Task<byte[]> IndiceCiegoAsync(TenantId tenant, CodigoClinico codigo, CancellationToken ct);
}
