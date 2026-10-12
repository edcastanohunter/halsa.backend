using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.Infrastructure.Clinico.Crypto;

/// <summary>
/// KEK local (archivo secreto de Docker en el VPS, o en línea en Development y tests).
/// Envuelve con AES-256-GCM. Formato de la DEK envuelta: <c>nonce (12) | tag (16) | ciphertext</c>.
/// </summary>
internal sealed class LocalFileKeyEncryptionProvider : IKeyEncryptionProvider
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[] _kek;

    public LocalFileKeyEncryptionProvider(IOptions<ClinicoOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ClinicoOptions value = options.Value;

        if (!KekLoader.TryLoad(value, out _kek, out string error))
        {
            throw new InvalidOperationException(error);
        }

        KekId = value.KekId;
    }

    public string KekId { get; }

    public byte[] Envolver(ReadOnlySpan<byte> dek, ReadOnlySpan<byte> aad)
    {
        byte[] envuelta = new byte[NonceBytes + TagBytes + dek.Length];
        Span<byte> nonce = envuelta.AsSpan(0, NonceBytes);
        Span<byte> tag = envuelta.AsSpan(NonceBytes, TagBytes);
        Span<byte> ciphertext = envuelta.AsSpan(NonceBytes + TagBytes);

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_kek, TagBytes);
        aes.Encrypt(nonce, dek, ciphertext, tag, aad);

        return envuelta;
    }

    public byte[] Desenvolver(ReadOnlySpan<byte> dekEnvuelta, ReadOnlySpan<byte> aad)
    {
        if (dekEnvuelta.Length <= NonceBytes + TagBytes)
        {
            throw new CryptographicException("La DEK envuelta está truncada.");
        }

        ReadOnlySpan<byte> nonce = dekEnvuelta[..NonceBytes];
        ReadOnlySpan<byte> tag = dekEnvuelta.Slice(NonceBytes, TagBytes);
        ReadOnlySpan<byte> ciphertext = dekEnvuelta[(NonceBytes + TagBytes)..];

        byte[] dek = new byte[ciphertext.Length];

        using var aes = new AesGcm(_kek, TagBytes);
        aes.Decrypt(nonce, ciphertext, tag, dek, aad);

        return dek;
    }
}
