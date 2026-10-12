using System.Security.Cryptography;
using System.Text;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Infrastructure.Clinico.Crypto;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.IntegrationTests.Clinico;

/// <summary>
/// <see cref="DekStore"/> and <see cref="AesGcmClinicalCipher"/> against PostgreSQL. Each store is a
/// fresh instance, so its in-memory cache never hides what the database did.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class DekStoreTests(DatabaseFixture db)
{
    private DekStore NuevoStore() => new(
        db.ClinicalConnectionString,
        new LocalFileKeyEncryptionProvider(Options.Create(new ClinicoOptions { KekBase64 = db.KekBase64 })),
        TimeProvider.System);

    [Fact]
    public async Task ConcurrentFirstUses_ShouldConvergeOnTheSameKey()
    {
        var tenant = new TenantId("ARL_DEK_CONCURRENTE");
        using DekStore primero = NuevoStore();
        using DekStore segundo = NuevoStore();

        Dek[] deks = await Task.WhenAll(
            primero.ActivaAsync(tenant, PropositoLlave.Contenido, CancellationToken.None),
            segundo.ActivaAsync(tenant, PropositoLlave.Contenido, CancellationToken.None));

        deks[0].KeyId.ShouldBe("ARL_DEK_CONCURRENTE:CONTENIDO:v1");
        deks[1].KeyId.ShouldBe(deks[0].KeyId);
        deks[1].Material.ShouldBe(deks[0].Material);
    }

    [Fact]
    public async Task PorKeyId_ShouldReturnTheSameKey_FromAnotherProcess()
    {
        var tenant = new TenantId("ARL_DEK_RELECTURA");
        using DekStore escritor = NuevoStore();
        using DekStore lector = NuevoStore();

        Dek activa = await escritor.ActivaAsync(tenant, PropositoLlave.Indice, CancellationToken.None);
        Dek leida = await lector.PorKeyIdAsync(tenant, activa.KeyId, CancellationToken.None);

        leida.Material.ShouldBe(activa.Material);
    }

    [Fact]
    public async Task PorKeyId_ShouldRefuseAnotherTenantsKey()
    {
        using DekStore store = NuevoStore();
        Dek deA = await store.ActivaAsync(new TenantId("ARL_A"), PropositoLlave.Contenido, CancellationToken.None);

        await Should.ThrowAsync<CryptographicException>(
            () => store.PorKeyIdAsync(new TenantId("ARL_B"), deA.KeyId, CancellationToken.None));
    }

    [Fact]
    public async Task Ciphertext_CopiedToAnotherRecord_ShouldNotDecrypt()
    {
        using DekStore store = NuevoStore();
        var cipher = new AesGcmClinicalCipher(store);
        var tenant = new TenantId("ARL_A");
        Guid registro = Guid.CreateVersion7();

        CampoCifrado cifrado = await cipher.CifrarAsync(
            tenant, registro, "contenido", Encoding.UTF8.GetBytes("texto clínico"), CancellationToken.None);

        (await cipher.DescifrarAsync(tenant, registro, "contenido", cifrado, CancellationToken.None))
            .ShouldBe(Encoding.UTF8.GetBytes("texto clínico"));

        await Should.ThrowAsync<AuthenticationTagMismatchException>(() =>
            cipher.DescifrarAsync(tenant, Guid.CreateVersion7(), "contenido", cifrado, CancellationToken.None));
    }

    [Fact]
    public async Task WrappedDek_ShouldNotUnwrapWithAnotherKek()
    {
        var tenant = new TenantId("ARL_DEK_OTRA_KEK");
        using DekStore store = NuevoStore();
        Dek dek = await store.ActivaAsync(tenant, PropositoLlave.Contenido, CancellationToken.None);

        var otraKek = new LocalFileKeyEncryptionProvider(Options.Create(new ClinicoOptions
        {
            KekBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        }));
        using var conOtraKek = new DekStore(db.ClinicalConnectionString, otraKek, TimeProvider.System);

        await Should.ThrowAsync<AuthenticationTagMismatchException>(
            () => conOtraKek.PorKeyIdAsync(tenant, dek.KeyId, CancellationToken.None));
    }
}
