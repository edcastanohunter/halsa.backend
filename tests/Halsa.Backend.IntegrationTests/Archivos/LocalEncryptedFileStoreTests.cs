using System.Security.Cryptography;
using System.Text;
using Halsa.Backend.Application.Abstractions.Archivos;
using Halsa.Backend.Infrastructure.Archivos;
using Halsa.Backend.Infrastructure.Clinico.Crypto;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.IntegrationTests.Archivos;

/// <summary>
/// SPEC 07, step 3: <see cref="LocalEncryptedFileStore"/> with the real DEKs of PostgreSQL, in a temporary folder.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class LocalEncryptedFileStoreTests(DatabaseFixture db) : IDisposable
{
    private const string TextoDelPdf = "Incapacidad por fractura de falange - texto confidencial";

    private static readonly TenantId ArlA = new("ARL_A");
    private static readonly TenantId ArlB = new("ARL_B");

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes(
        $"%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\nBT ({TextoDelPdf}) ET\n%%EOF\n");

    private readonly string _raiz = Path.Combine(Path.GetTempPath(), $"halsa-archivos-{Guid.NewGuid():N}");

    private LocalEncryptedFileStore Store() => new(
        db.Services.GetRequiredService<IDekStore>(),
        Options.Create(new ArchivosOptions { Raiz = _raiz }));

    public void Dispose()
    {
        if (Directory.Exists(_raiz))
        {
            Directory.Delete(_raiz, recursive: true);
        }
    }

    [Fact]
    public async Task GuardarYAbrir_ShouldRoundTripTheOriginal()
    {
        Guid id = Guid.CreateVersion7();

        ArchivoGuardado guardado = await Store().GuardarAsync(ArlA, id, new MemoryStream(Pdf), CancellationToken.None);

        guardado.KeyId.ShouldBe("ARL_A:SOPORTES:v1");
        guardado.Tamano.ShouldBe(Pdf.Length);
        guardado.Sha256Claro.ShouldBe(SHA256.HashData(Pdf));

        await using Stream abierto = await Store().AbrirAsync(ArlA, id, guardado.KeyId, CancellationToken.None);
        using var leido = new MemoryStream();
        await abierto.CopyToAsync(leido);
        leido.ToArray().ShouldBe(Pdf);
    }

    [Fact]
    public async Task TheFileOnDisk_ShouldBeEncrypted()
    {
        Guid id = Guid.CreateVersion7();

        ArchivoGuardado guardado = await Store().GuardarAsync(ArlA, id, new MemoryStream(Pdf), CancellationToken.None);

        byte[] enDisco = await File.ReadAllBytesAsync(Path.Combine(_raiz, guardado.Ruta));
        string comoTexto = Encoding.ASCII.GetString(enDisco);

        // nonce (12) ‖ ciphertext (same length as the original) ‖ tag (16).
        enDisco.Length.ShouldBe(12 + Pdf.Length + 16);
        comoTexto.ShouldNotStartWith("%PDF-");
        comoTexto.ShouldNotContain("%PDF-");
        comoTexto.ShouldNotContain(TextoDelPdf);
    }

    [Fact]
    public async Task TheRoute_ShouldBeTenantYearMonthId_FromTheUuidV7()
    {
        Guid id = Guid.CreateVersion7(new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero));

        ArchivoGuardado guardado = await Store().GuardarAsync(ArlA, id, new MemoryStream(Pdf), CancellationToken.None);

        guardado.Ruta.ShouldBe($"ARL_A/2026/03/{id}.bin");
        File.Exists(Path.Combine(_raiz, "ARL_A", "2026", "03", $"{id}.bin")).ShouldBeTrue();
    }

    [Fact]
    public async Task AFileCopiedToAnotherTenant_ShouldFailTheAad()
    {
        Guid id = Guid.CreateVersion7();
        ArchivoGuardado deA = await Store().GuardarAsync(ArlA, id, new MemoryStream(Pdf), CancellationToken.None);

        // ARL_B's own SOPORTES DEK, and A's file planted where B's would be: only the AAD can catch it.
        string keyIdDeB = (await db.Services.GetRequiredService<IDekStore>()
            .ActivaAsync(ArlB, PropositoLlave.Soportes, CancellationToken.None)).KeyId;
        string destino = Path.Combine(_raiz, LocalEncryptedFileStore.RutaRelativa(ArlB, id));
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        File.Copy(Path.Combine(_raiz, deA.Ruta), destino);

        await Should.ThrowAsync<CryptographicException>(
            () => Store().AbrirAsync(ArlB, id, keyIdDeB, CancellationToken.None));
    }

    [Fact]
    public async Task OpeningWithAnotherTenantsKey_ShouldBeRefused()
    {
        Guid id = Guid.CreateVersion7();
        ArchivoGuardado deA = await Store().GuardarAsync(ArlA, id, new MemoryStream(Pdf), CancellationToken.None);

        string destino = Path.Combine(_raiz, LocalEncryptedFileStore.RutaRelativa(ArlB, id));
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
        File.Copy(Path.Combine(_raiz, deA.Ruta), destino);

        // The DEK store refuses A's key for B before any decryption is attempted.
        await Should.ThrowAsync<CryptographicException>(
            () => Store().AbrirAsync(ArlB, id, deA.KeyId, CancellationToken.None));
    }

    [Fact]
    public async Task ATamperedFile_ShouldFailGcmAuthentication()
    {
        Guid id = Guid.CreateVersion7();
        ArchivoGuardado guardado = await Store().GuardarAsync(ArlA, id, new MemoryStream(Pdf), CancellationToken.None);

        string ruta = Path.Combine(_raiz, guardado.Ruta);
        byte[] enDisco = await File.ReadAllBytesAsync(ruta);
        enDisco[20] ^= 0xFF;
        await File.WriteAllBytesAsync(ruta, enDisco);

        await Should.ThrowAsync<CryptographicException>(
            () => Store().AbrirAsync(ArlA, id, guardado.KeyId, CancellationToken.None));
    }

    [Fact]
    public async Task ANonV7Id_ShouldBeRejected()
    {
        await Should.ThrowAsync<ArgumentException>(
            () => Store().GuardarAsync(ArlA, Guid.NewGuid(), new MemoryStream(Pdf), CancellationToken.None));
    }
}
