using System.Globalization;
using System.Security.Cryptography;
using Halsa.Backend.Application.Abstractions.Archivos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Infrastructure.Clinico.Crypto;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.Infrastructure.Archivos;

/// <summary>Sección <c>Archivos</c>.</summary>
public sealed class ArchivosOptions
{
    public const string SectionName = "Archivos";

    /// <summary>
    /// Desarrollo: <c>./.data/archivos</c> (en <c>.gitignore</c>), relativa a la raíz de contenido de la API. VPS: el
    /// volumen <c>halsa.backend-archivos</c>.
    /// </summary>
    public string Raiz { get; set; } = "./.data/archivos";
}

/// <summary>
/// <see cref="IFileStore"/> en un volumen local. Cada archivo es <c>nonce ‖ ciphertext ‖ tag</c> (AES-256-GCM) con la
/// DEK <see cref="PropositoLlave.Soportes"/> del tenant y AAD <c>"{tenant}|{archivoId}|soporte"</c>: copiarlo a otro
/// tenant o a otro id hace fallar el descifrado. La ruta es <c>{Raiz}/{tenant}/{yyyy}/{MM}/{archivoId}.bin</c>, con la
/// fecha del UUID v7, y se escribe en un temporal que luego se renombra: nunca queda un archivo a medias.
///
/// Todo el archivo pasa por memoria una vez (el endpoint limita la parte a 10 MB).
/// </summary>
internal sealed class LocalEncryptedFileStore(
    IDekStore dekStore,
    IOptions<ArchivosOptions> options,
    IHostEnvironment? environment = null)
    : IFileStore
{
    /// <summary>El campo de la AAD.</summary>
    public const string CampoSoporte = "soporte";

    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly string _raiz = Path.GetFullPath(options.Value.Raiz, environment?.ContentRootPath ?? Directory.GetCurrentDirectory());

    public async Task<ArchivoGuardado> GuardarAsync(TenantId tenant, Guid archivoId, Stream contenido, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contenido);

        string ruta = RutaRelativa(tenant, archivoId);

        using var claro = new MemoryStream();
        await contenido.CopyToAsync(claro, ct);
        byte[] bytes = claro.GetBuffer();
        var original = new ReadOnlyMemory<byte>(bytes, 0, (int)claro.Length);

        try
        {
            byte[] sha256 = SHA256.HashData(original.Span);

            CampoCifrado cifrado = await AesGcmCampo.CifrarAsync(
                dekStore, tenant, PropositoLlave.Soportes, archivoId, CampoSoporte, original, ct);

            string destino = Absoluta(ruta);
            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);

            string temporal = $"{destino}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var archivo = new FileStream(temporal, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await archivo.WriteAsync(cifrado.Nonce, ct);
                    await archivo.WriteAsync(cifrado.Ciphertext, ct);
                    await archivo.WriteAsync(cifrado.Tag, ct);
                }

                File.Move(temporal, destino, overwrite: false);
            }
            finally
            {
                File.Delete(temporal);
            }

            return new ArchivoGuardado(ruta, cifrado.KeyId, sha256, original.Length);
        }
        finally
        {
            // The plain PDF carries the diagnosis: do not leave it in a pooled buffer longer than needed.
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task<Stream> AbrirAsync(TenantId tenant, Guid archivoId, string keyId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        byte[] guardado = await File.ReadAllBytesAsync(Absoluta(RutaRelativa(tenant, archivoId)), ct);
        if (guardado.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("El archivo cifrado está truncado.");
        }

        var cifrado = new CampoCifrado(
            guardado[NonceBytes..^TagBytes],
            guardado[..NonceBytes],
            guardado[^TagBytes..],
            keyId);

        byte[] claro = await AesGcmCampo.DescifrarAsync(dekStore, tenant, archivoId, CampoSoporte, cifrado, ct);

        return new MemoryStream(claro, writable: false);
    }

    /// <summary><c>{tenant}/{yyyy}/{MM}/{archivoId}.bin</c>, con el año y el mes (UTC) de la marca de tiempo del UUID v7.</summary>
    internal static string RutaRelativa(TenantId tenant, Guid archivoId)
    {
        if (archivoId.Version != 7)
        {
            throw new ArgumentException("El id de un archivo debe ser un UUID v7.", nameof(archivoId));
        }

        DateTimeOffset creado = MarcaDeTiempo(archivoId);

        return string.Create(CultureInfo.InvariantCulture,
            $"{tenant.Value}/{creado:yyyy}/{creado:MM}/{archivoId}.bin");
    }

    /// <summary>Los primeros 48 bits de un UUID v7: milisegundos Unix.</summary>
    private static DateTimeOffset MarcaDeTiempo(Guid uuidV7)
    {
        Span<byte> bytes = stackalloc byte[16];
        uuidV7.TryWriteBytes(bytes, bigEndian: true, out _);

        long milisegundos = 0;
        for (int i = 0; i < 6; i++)
        {
            milisegundos = (milisegundos << 8) | bytes[i];
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milisegundos);
    }

    private string Absoluta(string rutaRelativa)
    {
        string absoluta = Path.GetFullPath(Path.Combine(_raiz, rutaRelativa));

        // The tenant code is validated upstream; this is the belt to those braces.
        if (!absoluta.StartsWith(_raiz, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("La ruta del archivo sale de la raíz del almacenamiento.");
        }

        return absoluta;
    }
}
