using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Infrastructure.Clinico.Crypto;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.UnitTests.Clinico;

public sealed class AesGcmClinicalCipherTests
{
    private static readonly TenantId ArlA = new("ARL_A");
    private static readonly TenantId ArlB = new("ARL_B");
    private static readonly byte[] Claro = Encoding.UTF8.GetBytes("FRACTURA-MARCADOR-7731");

    private readonly AesGcmClinicalCipher _cipher = new(new InMemoryDekStore());

    private Task<CampoCifrado> CifrarAsync(Guid registro, string campo = "contenido") =>
        _cipher.CifrarAsync(ArlA, registro, campo, Claro, CancellationToken.None);

    // ── Cifrar / Descifrar ────────────────────────────────────────────────────

    [Fact]
    public async Task Descifrar_ShouldReturnTheClearText()
    {
        Guid registro = Guid.CreateVersion7();
        CampoCifrado cifrado = await CifrarAsync(registro);

        cifrado.KeyId.ShouldBe("ARL_A:CONTENIDO:v1");
        cifrado.Nonce.Length.ShouldBe(12);
        cifrado.Tag.Length.ShouldBe(16);
        cifrado.Ciphertext.ShouldNotBe(Claro);

        (await _cipher.DescifrarAsync(ArlA, registro, "contenido", cifrado, CancellationToken.None)).ShouldBe(Claro);
    }

    [Fact]
    public async Task Cifrar_ShouldUseAFreshNonceForEveryField()
    {
        Guid registro = Guid.CreateVersion7();

        CampoCifrado primero = await CifrarAsync(registro);
        CampoCifrado segundo = await CifrarAsync(registro);

        segundo.Nonce.ShouldNotBe(primero.Nonce);
        segundo.Ciphertext.ShouldNotBe(primero.Ciphertext);
    }

    [Fact]
    public async Task Descifrar_ShouldFail_ForAnotherRecord()
    {
        CampoCifrado cifrado = await CifrarAsync(Guid.CreateVersion7());

        await Should.ThrowAsync<AuthenticationTagMismatchException>(() =>
            _cipher.DescifrarAsync(ArlA, Guid.CreateVersion7(), "contenido", cifrado, CancellationToken.None));
    }

    [Fact]
    public async Task Descifrar_ShouldFail_ForAnotherField()
    {
        Guid registro = Guid.CreateVersion7();
        CampoCifrado cifrado = await CifrarAsync(registro, "diagnostico");

        await Should.ThrowAsync<AuthenticationTagMismatchException>(() =>
            _cipher.DescifrarAsync(ArlA, registro, "contenido", cifrado, CancellationToken.None));
    }

    [Fact]
    public async Task Descifrar_ShouldFail_ForAnotherTenant()
    {
        Guid registro = Guid.CreateVersion7();
        CampoCifrado cifrado = await CifrarAsync(registro);

        await Should.ThrowAsync<CryptographicException>(() =>
            _cipher.DescifrarAsync(ArlB, registro, "contenido", cifrado, CancellationToken.None));
    }

    [Fact]
    public async Task Descifrar_ShouldFail_WhenTheCiphertextWasTamperedWith()
    {
        Guid registro = Guid.CreateVersion7();
        CampoCifrado cifrado = await CifrarAsync(registro);
        cifrado.Ciphertext[0] ^= 0xFF;

        await Should.ThrowAsync<AuthenticationTagMismatchException>(() =>
            _cipher.DescifrarAsync(ArlA, registro, "contenido", cifrado, CancellationToken.None));
    }

    // ── Índice ciego ──────────────────────────────────────────────────────────

    [Fact]
    public async Task IndiceCiego_ShouldBeDeterministic_AndIgnoreCaseAndSpaces()
    {
        byte[] uno = await _cipher.IndiceCiegoAsync(ArlA, new CodigoClinico("CIE-10", "S62.6"), CancellationToken.None);
        byte[] dos = await _cipher.IndiceCiegoAsync(ArlA, new CodigoClinico(" cie-10", "s62. 6 "), CancellationToken.None);

        uno.Length.ShouldBe(32);
        dos.ShouldBe(uno);
    }

    [Fact]
    public async Task IndiceCiego_ShouldDifferAcrossTenantsAndCodes()
    {
        var fractura = new CodigoClinico("CIE-10", "S62.6");

        byte[] enA = await _cipher.IndiceCiegoAsync(ArlA, fractura, CancellationToken.None);
        byte[] enB = await _cipher.IndiceCiegoAsync(ArlB, fractura, CancellationToken.None);
        byte[] otroCodigo = await _cipher.IndiceCiegoAsync(ArlA, new CodigoClinico("CIE-10", "S62.7"), CancellationToken.None);

        enB.ShouldNotBe(enA);
        otroCodigo.ShouldNotBe(enA);
    }

    [Fact]
    public async Task IndiceCiego_ShouldUseTheIndexKey_NotTheContentKey()
    {
        var store = new InMemoryDekStore();
        var cipher = new AesGcmClinicalCipher(store);

        await cipher.IndiceCiegoAsync(ArlA, new CodigoClinico("CIE-10", "S62.6"), CancellationToken.None);

        store.Pedidas.ShouldBe(["ARL_A:INDICE:v1"]);
    }

    /// <summary>One random DEK per (tenant, purpose), as <see cref="DekStore"/> keeps them, without a database.</summary>
    private sealed class InMemoryDekStore : IDekStore
    {
        private readonly ConcurrentDictionary<string, Dek> _deks = new();

        public List<string> Pedidas { get; } = [];

        public Task<Dek> ActivaAsync(TenantId tenant, string proposito, CancellationToken ct)
        {
            string keyId = $"{tenant.Value}:{proposito}:v1";
            Pedidas.Add(keyId);
            return Task.FromResult(_deks.GetOrAdd(keyId, id => new Dek(id, RandomNumberGenerator.GetBytes(32))));
        }

        public Task<Dek> PorKeyIdAsync(TenantId tenant, string keyId, CancellationToken ct) =>
            keyId.StartsWith($"{tenant.Value}:", StringComparison.Ordinal) && _deks.TryGetValue(keyId, out Dek? dek)
                ? Task.FromResult(dek)
                : throw new CryptographicException($"La DEK '{keyId}' no es del tenant {tenant.Value}.");
    }
}
