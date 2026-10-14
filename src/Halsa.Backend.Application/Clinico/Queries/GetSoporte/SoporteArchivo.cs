using System.Security.Cryptography;
using Halsa.Backend.Application.Abstractions.Archivos;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Clinico.Queries.GetSoporte;

/// <summary>
/// Abre el soporte descifrado y lo verifica contra <c>sha256_claro</c> antes de entregar un solo byte (SPEC 08).
/// El archivo pasa entero por memoria: el límite de carga es 10 MB (SPEC 07).
/// </summary>
public static class SoporteArchivo
{
    public static async Task<Result<byte[]>> AbrirVerificadoAsync(
        IFileStore archivos, SoporteDescargable soporte, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(archivos);
        ArgumentNullException.ThrowIfNull(soporte);

        byte[] claro;
        try
        {
            await using Stream stream = await archivos.AbrirAsync(soporte.TenantId, soporte.SoporteId, soporte.KeyId, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            claro = buffer.ToArray();
        }
        catch (Exception exception) when (exception is CryptographicException or FileNotFoundException or DirectoryNotFoundException)
        {
            // An altered file fails the GCM tag; a missing one is just as unusable. Neither is the user's fault.
            return Result.Failure<byte[]>(SoporteErrors.Corrupto);
        }

        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(claro), soporte.Sha256Claro))
        {
            CryptographicOperations.ZeroMemory(claro);
            return Result.Failure<byte[]>(SoporteErrors.Corrupto);
        }

        return claro;
    }
}
