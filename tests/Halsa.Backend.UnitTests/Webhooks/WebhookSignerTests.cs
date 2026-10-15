using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Halsa.Backend.Domain.Webhooks;
using Halsa.Backend.Infrastructure.Clinico.Crypto;
using Halsa.Backend.Infrastructure.Webhooks;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.UnitTests.Webhooks;

/// <summary>SPEC 10, paso 2: la firma <c>Halsa-Signature</c> y el secreto envuelto con la KEK.</summary>
public sealed class WebhookSignerTests
{
    private static readonly byte[] Secreto = Encoding.ASCII.GetBytes("halsa-webhook-secreto-de-prueba!");

    private static readonly byte[] Anterior = Encoding.ASCII.GetBytes("halsa-webhook-secreto-anterior!!");

    private const string Cuerpo = """{"eventoId":"0199e3a0-0000-7000-8000-000000000001","tipo":"plazo.vencido"}""";

    private const long T = 1_760_486_400;

    // Computed outside .NET: hmac.new(secreto, b"1760486400." + cuerpo, sha256).hexdigest()
    private const string V1 = "bb0837a80dbee30d2ba8b9836d20069aabd7f7f421fa97276527f604006a76a1";

    private const string V1Anterior = "e83664ff8212cdd0dc1ff2301e83dbb4251758c72b9dbd47bfacf0a2add41d98";

    // ── Firma ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Firmar_ShouldMatchAFixedVector()
    {
        WebhookSigner.Firmar(T, Cuerpo, Secreto).ShouldBe($"t={T},v1={V1}");
    }

    [Fact]
    public void Firmar_DuringARotation_ShouldSendTheNewSignatureFirstAndThenThePreviousOne()
    {
        WebhookSigner.Firmar(T, Cuerpo, Secreto, Anterior).ShouldBe($"t={T},v1={V1},v1={V1Anterior}");
    }

    [Fact]
    public void Firmar_ShouldCoverTheTimestamp()
    {
        WebhookSigner.Firmar(T + 1, Cuerpo, Secreto).ShouldNotContain(V1);
    }

    [Fact]
    public void Firmar_ShouldCoverEveryByteOfTheBody()
    {
        WebhookSigner.Firmar(T, Cuerpo.Replace("vencido", "vencida", StringComparison.Ordinal), Secreto)
            .ShouldNotContain(V1);
    }

    // ── Secreto ───────────────────────────────────────────────────────────────

    [Fact]
    public void Generar_ShouldReturn32RandomBytesInBase64Url_WrappedWithTheKek()
    {
        WebhookSecretProtector protector = Protector();
        SuscripcionWebhookId id = SuscripcionWebhookId.New();

        SecretoNuevoDe(protector, id, out byte[] claro, out byte[] ct, out string keyId);

        claro.Length.ShouldBe(32);
        keyId.ShouldBe("local:v1");
        ct.AsSpan().IndexOf(claro).ShouldBe(-1);
        protector.Desproteger(id, ct, keyId).ShouldBe(claro);
    }

    [Fact]
    public void Desproteger_WithAnotherSubscriptionId_ShouldFail()
    {
        WebhookSecretProtector protector = Protector();
        SuscripcionWebhookId id = SuscripcionWebhookId.New();

        SecretoNuevoDe(protector, id, out _, out byte[] ct, out string keyId);

        Should.Throw<CryptographicException>(() => protector.Desproteger(SuscripcionWebhookId.New(), ct, keyId));
    }

    [Fact]
    public void Reenvolver_ShouldKeepTheSameSecretUnderANewNonce()
    {
        WebhookSecretProtector protector = Protector();
        SuscripcionWebhookId id = SuscripcionWebhookId.New();
        SecretoNuevoDe(protector, id, out byte[] claro, out byte[] ct, out string keyId);

        byte[] otra = protector.Reenvolver(id, ct, keyId);

        otra.ShouldNotBe(ct);
        protector.Desproteger(id, otra, keyId).ShouldBe(claro);
    }

    private static WebhookSecretProtector Protector() => new(new LocalFileKeyEncryptionProvider(Options.Create(
        new ClinicoOptions { KekBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) })));

    private static void SecretoNuevoDe(
        WebhookSecretProtector protector, SuscripcionWebhookId id, out byte[] claro, out byte[] ct, out string keyId)
    {
        var nuevo = protector.Generar(id);
        claro = Base64Url.DecodeFromChars(nuevo.Secreto);
        ct = nuevo.SecretoCt;
        keyId = nuevo.KeyId;
    }
}
