namespace Halsa.Backend.Infrastructure.Clinico.Crypto;

/// <summary>
/// La llave maestra (KEK) que envuelve las DEK de cada tenant. Vive solo en Infrastructure: la
/// aplicación nunca la ve. Hoy es un archivo secreto en el VPS; mañana puede ser OpenBao o Vault
/// sin tocar nada más que esta implementación.
/// </summary>
public interface IKeyEncryptionProvider
{
    /// <summary>Se guarda junto a cada DEK envuelta, para que una KEK nueva pueda convivir con la anterior.</summary>
    string KekId { get; }

    byte[] Envolver(ReadOnlySpan<byte> dek, ReadOnlySpan<byte> aad);

    /// <summary>Lanza una <see cref="System.Security.Cryptography.CryptographicException"/> si la AAD no coincide.</summary>
    byte[] Desenvolver(ReadOnlySpan<byte> dekEnvuelta, ReadOnlySpan<byte> aad);
}
