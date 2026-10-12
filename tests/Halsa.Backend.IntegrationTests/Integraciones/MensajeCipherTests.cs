using System.Security.Cryptography;
using System.Text;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Infrastructure.Clinico.Crypto;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.IntegrationTests.Integraciones;

/// <summary>
/// SPEC 05, paso 2: el mensaje crudo se cifra con la DEK <c>INTEGRACION</c> del tenant y AAD
/// <c>"{tenant}|{solicitudId}|mensaje"</c>.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class MensajeCipherTests(DatabaseFixture db)
{
    private static readonly byte[] Cuerpo = Encoding.UTF8.GetBytes("""{"descripcion":"caída del andamio"}""");

    private DekStore NuevoStore() => new(
        db.ClinicalConnectionString,
        new LocalFileKeyEncryptionProvider(Options.Create(new ClinicoOptions { KekBase64 = db.KekBase64 })),
        TimeProvider.System);

    [Fact]
    public async Task Mensaje_ShouldDecryptWithItsAad_AndUseTheIntegracionKey()
    {
        using DekStore store = NuevoStore();
        var cipher = new AesGcmMensajeCipher(store);
        var tenant = new TenantId("ARL_A");
        Guid solicitud = Guid.CreateVersion7();

        CampoCifrado cifrado = await cipher.CifrarAsync(tenant, solicitud, Cuerpo, CancellationToken.None);

        cifrado.KeyId.ShouldBe("ARL_A:INTEGRACION:v1");
        (await cipher.DescifrarAsync(tenant, solicitud, cifrado, CancellationToken.None)).ShouldBe(Cuerpo);
    }

    [Fact]
    public async Task Mensaje_WithAnotherSolicitudId_ShouldNotDecrypt()
    {
        using DekStore store = NuevoStore();
        var cipher = new AesGcmMensajeCipher(store);
        var tenant = new TenantId("ARL_A");

        CampoCifrado cifrado = await cipher.CifrarAsync(tenant, Guid.CreateVersion7(), Cuerpo, CancellationToken.None);

        await Should.ThrowAsync<AuthenticationTagMismatchException>(
            () => cipher.DescifrarAsync(tenant, Guid.CreateVersion7(), cifrado, CancellationToken.None));
    }

    [Fact]
    public async Task Mensaje_AsAnotherTenant_ShouldNotDecrypt()
    {
        using DekStore store = NuevoStore();
        var cipher = new AesGcmMensajeCipher(store);
        Guid solicitud = Guid.CreateVersion7();

        CampoCifrado cifrado = await cipher.CifrarAsync(new TenantId("ARL_A"), solicitud, Cuerpo, CancellationToken.None);

        await Should.ThrowAsync<CryptographicException>(
            () => cipher.DescifrarAsync(new TenantId("ARL_B"), solicitud, cifrado, CancellationToken.None));
    }

    [Fact]
    public async Task IntegracionKey_ShouldDifferFromTheContenidoKey()
    {
        using DekStore store = NuevoStore();
        var tenant = new TenantId("ARL_DEK_PROPOSITOS");

        Dek contenido = await store.ActivaAsync(tenant, PropositoLlave.Contenido, CancellationToken.None);
        Dek integracion = await store.ActivaAsync(tenant, PropositoLlave.Integracion, CancellationToken.None);

        integracion.KeyId.ShouldNotBe(contenido.KeyId);
        integracion.Material.ShouldNotBe(contenido.Material);
    }
}
