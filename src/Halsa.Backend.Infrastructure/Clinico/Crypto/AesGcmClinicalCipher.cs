using System.Security.Cryptography;
using System.Text;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Integraciones;
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
    public Task<CampoCifrado> CifrarAsync(
        TenantId tenant, Guid registroId, string campo, ReadOnlyMemory<byte> claro, CancellationToken ct) =>
        AesGcmCampo.CifrarAsync(dekStore, tenant, PropositoLlave.Contenido, registroId, campo, claro, ct);

    public Task<byte[]> DescifrarAsync(
        TenantId tenant, Guid registroId, string campo, CampoCifrado cifrado, CancellationToken ct) =>
        AesGcmCampo.DescifrarAsync(dekStore, tenant, registroId, campo, cifrado, ct);

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
}

/// <summary>
/// El mensaje crudo de una integración (SPEC 05): el mismo AES-256-GCM que el contenido clínico, con la DEK
/// <see cref="PropositoLlave.Integracion"/> y AAD <c>"{tenant}|{solicitudId}|mensaje"</c>.
/// </summary>
internal sealed class AesGcmMensajeCipher(IDekStore dekStore) : IMensajeCipher
{
    /// <summary>El campo de la AAD.</summary>
    public const string CampoMensaje = "mensaje";

    public Task<CampoCifrado> CifrarAsync(
        TenantId tenant, Guid solicitudId, ReadOnlyMemory<byte> cuerpo, CancellationToken ct) =>
        AesGcmCampo.CifrarAsync(dekStore, tenant, PropositoLlave.Integracion, solicitudId, CampoMensaje, cuerpo, ct);

    public Task<byte[]> DescifrarAsync(TenantId tenant, Guid solicitudId, CampoCifrado cifrado, CancellationToken ct) =>
        AesGcmCampo.DescifrarAsync(dekStore, tenant, solicitudId, CampoMensaje, cifrado, ct);
}

/// <summary>El cifrado de un campo, común al contenido clínico y al mensaje de integración.</summary>
internal static class AesGcmCampo
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static async Task<CampoCifrado> CifrarAsync(
        IDekStore dekStore,
        TenantId tenant,
        string proposito,
        Guid filaId,
        string campo,
        ReadOnlyMemory<byte> claro,
        CancellationToken ct)
    {
        Dek dek = await dekStore.ActivaAsync(tenant, proposito, ct);

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        byte[] tag = new byte[TagBytes];
        byte[] ciphertext = new byte[claro.Length];

        using var aes = new AesGcm(dek.Material, TagBytes);
        aes.Encrypt(nonce, claro.Span, ciphertext, tag, Aad(tenant, filaId, campo));

        return new CampoCifrado(ciphertext, nonce, tag, dek.KeyId);
    }

    /// <summary>La DEK sale del <c>KeyId</c> del campo, así que el propósito no hace falta para descifrar.</summary>
    public static async Task<byte[]> DescifrarAsync(
        IDekStore dekStore, TenantId tenant, Guid filaId, string campo, CampoCifrado cifrado, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cifrado);

        if (cifrado.Nonce.Length != NonceBytes || cifrado.Tag.Length != TagBytes)
        {
            throw new CryptographicException("El campo cifrado tiene un nonce o un tag de tamaño inválido.");
        }

        Dek dek = await dekStore.PorKeyIdAsync(tenant, cifrado.KeyId, ct);

        byte[] claro = new byte[cifrado.Ciphertext.Length];

        using var aes = new AesGcm(dek.Material, TagBytes);
        aes.Decrypt(cifrado.Nonce, cifrado.Ciphertext, cifrado.Tag, claro, Aad(tenant, filaId, campo));

        return claro;
    }

    private static byte[] Aad(TenantId tenant, Guid filaId, string campo) =>
        Encoding.UTF8.GetBytes($"{tenant.Value}|{filaId}|{campo}");
}
