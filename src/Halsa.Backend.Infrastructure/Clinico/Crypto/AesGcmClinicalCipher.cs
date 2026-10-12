using System.Security.Cryptography;
using System.Text;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Infrastructure.Clinico.Crypto;

/// <summary>
/// AES-256-GCM por campo: nonce aleatorio de 12 bytes, tag de 16 y AAD
/// <c>"{tenantId}|{registroId}|{campo}"</c>. La AAD ata el ciphertext a su fila, así que copiarlo
/// a otro registro, a otro campo o a otro tenant hace fallar el descifrado.
/// </summary>
internal sealed class AesGcmClinicalCipher(IDekStore dekStore) : IClinicalCipher
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public async Task<CampoCifrado> CifrarAsync(
        TenantId tenant, Guid registroId, string campo, ReadOnlyMemory<byte> claro, CancellationToken ct)
    {
        Dek dek = await dekStore.ActivaAsync(tenant, PropositoLlave.Contenido, ct);

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] tag = new byte[TagBytes];
        byte[] ciphertext = new byte[claro.Length];

        using var aes = new AesGcm(dek.Material, TagBytes);
        aes.Encrypt(nonce, claro.Span, ciphertext, tag, Aad(tenant, registroId, campo));

        return new CampoCifrado(ciphertext, nonce, tag, dek.KeyId);
    }

    public async Task<byte[]> DescifrarAsync(
        TenantId tenant, Guid registroId, string campo, CampoCifrado cifrado, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cifrado);

        if (cifrado.Nonce.Length != NonceBytes || cifrado.Tag.Length != TagBytes)
        {
            throw new CryptographicException("El campo cifrado tiene un nonce o un tag de tamaño inválido.");
        }

        Dek dek = await dekStore.PorKeyIdAsync(tenant, cifrado.KeyId, ct);

        byte[] claro = new byte[cifrado.Ciphertext.Length];

        using var aes = new AesGcm(dek.Material, TagBytes);
        aes.Decrypt(cifrado.Nonce, cifrado.Ciphertext, cifrado.Tag, claro, Aad(tenant, registroId, campo));

        return claro;
    }

    public async Task<byte[]> IndiceCiegoAsync(TenantId tenant, CodigoClinico codigo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(codigo);

        Dek dek = await dekStore.ActivaAsync(tenant, PropositoLlave.Indice, ct);

        byte[] datos = Encoding.UTF8.GetBytes($"{Normalizar(codigo.Sistema)}|{Normalizar(codigo.Codigo)}");
        return HMACSHA256.HashData(dek.Material, datos);
    }

    /// <summary>Mayúsculas y sin espacios, para que <c>"cie-10 | s62.6"</c> y <c>"CIE-10|S62.6"</c> coincidan.</summary>
    internal static string Normalizar(string valor) =>
        string.Concat(valor.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    private static byte[] Aad(TenantId tenant, Guid registroId, string campo) =>
        Encoding.UTF8.GetBytes($"{tenant.Value}|{registroId}|{campo}");
}
