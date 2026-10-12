using System.Security.Cryptography;
using System.Text;
using Halsa.Backend.Infrastructure.Clinico.Crypto;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.UnitTests.Clinico;

public sealed class LocalFileKeyEncryptionProviderTests
{
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("ARL_A|CONTENIDO|ARL_A:CONTENIDO:v1");

    private static string NuevaKek() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static LocalFileKeyEncryptionProvider Provider(string? kekBase64 = null) =>
        new(Options.Create(new ClinicoOptions { KekBase64 = kekBase64 ?? NuevaKek() }));

    // ── Envolver / Desenvolver ────────────────────────────────────────────────

    [Fact]
    public void Desenvolver_ShouldReturnTheOriginalDek()
    {
        LocalFileKeyEncryptionProvider provider = Provider();
        byte[] dek = RandomNumberGenerator.GetBytes(32);

        byte[] envuelta = provider.Envolver(dek, Aad);

        envuelta.ShouldNotBe(dek);
        provider.Desenvolver(envuelta, Aad).ShouldBe(dek);
    }

    [Fact]
    public void Envolver_ShouldUseAFreshNonceEachTime()
    {
        LocalFileKeyEncryptionProvider provider = Provider();
        byte[] dek = RandomNumberGenerator.GetBytes(32);

        provider.Envolver(dek, Aad).ShouldNotBe(provider.Envolver(dek, Aad));
    }

    [Fact]
    public void Desenvolver_ShouldFail_WhenTheAadIsDifferent()
    {
        LocalFileKeyEncryptionProvider provider = Provider();
        byte[] envuelta = provider.Envolver(RandomNumberGenerator.GetBytes(32), Aad);

        byte[] otraAad = Encoding.UTF8.GetBytes("ARL_B|CONTENIDO|ARL_B:CONTENIDO:v1");

        Should.Throw<AuthenticationTagMismatchException>(() => provider.Desenvolver(envuelta, otraAad));
    }

    [Fact]
    public void Desenvolver_ShouldFail_WithAnotherKek()
    {
        byte[] envuelta = Provider().Envolver(RandomNumberGenerator.GetBytes(32), Aad);

        Should.Throw<AuthenticationTagMismatchException>(() => Provider().Desenvolver(envuelta, Aad));
    }

    [Fact]
    public void Provider_ShouldReadTheKekFromKekPath()
    {
        string path = Path.GetTempFileName();
        try
        {
            string kek = NuevaKek();
            File.WriteAllText(path, kek + "\n");

            var desdeArchivo = new LocalFileKeyEncryptionProvider(
                Options.Create(new ClinicoOptions { KekPath = path }));
            byte[] dek = RandomNumberGenerator.GetBytes(32);

            // The same KEK given inline unwraps what the file-based provider wrapped.
            Provider(kek).Desenvolver(desdeArchivo.Envolver(dek, Aad), Aad).ShouldBe(dek);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Validación al arrancar ────────────────────────────────────────────────

    [Fact]
    public void Validator_ShouldRejectKekBase64_InProduction()
    {
        var validator = new ClinicoOptionsValidator("Production");

        ValidateOptionsResult result = validator.Validate(null, new ClinicoOptions { KekBase64 = NuevaKek() });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("KekBase64");
    }

    [Fact]
    public void Validator_ShouldAcceptKekBase64_OutsideProduction()
    {
        var validator = new ClinicoOptionsValidator("Development");

        validator.Validate(null, new ClinicoOptions { KekBase64 = NuevaKek() }).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validator_ShouldRejectAMissingKekPath_InProduction()
    {
        var validator = new ClinicoOptionsValidator("Production");

        ValidateOptionsResult result = validator.Validate(
            null, new ClinicoOptions { KekPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.kek") });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("no existe");
    }

    [Fact]
    public void Validator_ShouldRejectAKekThatIsNot32Bytes()
    {
        var validator = new ClinicoOptionsValidator("Development");

        ValidateOptionsResult result = validator.Validate(
            null, new ClinicoOptions { KekBase64 = Convert.ToBase64String(new byte[16]) });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("32 bytes");
    }

    [Fact]
    public void Validator_ShouldRejectMissingKek()
    {
        new ClinicoOptionsValidator(null).Validate(null, new ClinicoOptions()).Failed.ShouldBeTrue();
    }
}
