using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Abstractions.Archivos;

/// <summary>
/// El almacenamiento de archivos soporte (SPEC 07), cifrados con la DEK <c>SOPORTES</c> del tenant. Hoy es un volumen
/// local; migrar a almacenamiento de objetos es otra implementación de esta interfaz.
/// </summary>
public interface IFileStore
{
    /// <summary>
    /// Cifra y guarda <paramref name="contenido"/> (desde su posición actual). <paramref name="archivoId"/> debe ser un
    /// UUID v7: su marca de tiempo decide la carpeta <c>yyyy/MM</c>, así que el archivo se encuentra sin la ruta.
    /// </summary>
    Task<ArchivoGuardado> GuardarAsync(TenantId tenant, Guid archivoId, Stream contenido, CancellationToken ct);

    /// <summary>
    /// El contenido original. Lanza una <see cref="System.Security.Cryptography.CryptographicException"/> si el
    /// archivo no es de ese tenant e id (la AAD no coincide) o fue alterado, y <see cref="FileNotFoundException"/>
    /// si no existe.
    /// </summary>
    Task<Stream> AbrirAsync(TenantId tenant, Guid archivoId, string keyId, CancellationToken ct);
}

/// <param name="Ruta">Relativa a la raíz del almacenamiento.</param>
/// <param name="KeyId">La DEK que lo cifró.</param>
/// <param name="Sha256Claro">SHA-256 del contenido original.</param>
/// <param name="Tamano">Bytes del contenido original.</param>
public sealed record ArchivoGuardado(string Ruta, string KeyId, byte[] Sha256Claro, long Tamano);
